#!/usr/bin/env python3
"""Build and verify a small, indexed Stndr database from a Sefaria BSON dump.

This is deliberately a prototype, not application runtime code.  It proves that
the raw texts, version licences, work metadata, and link records can be converted
without running MongoDB.  It imports all work metadata, representative text
versions, and links for an explicit set of passage references.

Requires PyMongo's ``bson`` package (``python -m pip install pymongo``).
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sqlite3
import sys
import time
from collections import Counter, defaultdict
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable, Iterator

try:
    from bson import decode_file_iter
except ImportError as exc:  # pragma: no cover - environment guidance
    raise SystemExit(
        "The prototype needs PyMongo's bson package. Install it with: "
        "python -m pip install pymongo"
    ) from exc


SCHEMA_VERSION = "1"


@dataclass(frozen=True)
class TextStats:
    segments: int
    characters: int
    shape: str


def compact_json(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def json_hash(value: Any) -> str:
    return hashlib.sha256(compact_json(value).encode("utf-8")).hexdigest()


def walk_text(value: Any, path: tuple[str, ...] = ()) -> Iterator[tuple[tuple[str, ...], str]]:
    if isinstance(value, str):
        if value.strip():
            yield path, value
        return
    if isinstance(value, list):
        for index, child in enumerate(value, start=1):
            yield from walk_text(child, path + (str(index),))
        return
    if isinstance(value, dict):
        for key, child in value.items():
            yield from walk_text(child, path + (str(key),))


def text_stats(value: Any) -> TextStats:
    segments = 0
    characters = 0
    for _, text in walk_text(value):
        segments += 1
        characters += len(text)
    return TextStats(segments, characters, type(value).__name__)


def bson_documents(path: Path) -> Iterator[dict[str, Any]]:
    with path.open("rb") as stream:
        yield from decode_file_iter(stream)


def require_collection(dump_dir: Path, name: str) -> Path:
    path = dump_dir / f"{name}.bson"
    if not path.is_file():
        raise FileNotFoundError(f"Required collection not found: {path}")
    return path


def primary_title(schema: Any, language: str) -> str:
    if not isinstance(schema, dict):
        return ""
    fallback = ""
    for title in schema.get("titles", []):
        if not isinstance(title, dict) or title.get("lang") != language:
            continue
        text = str(title.get("text") or "")
        fallback = fallback or text
        if title.get("primary"):
            return text
    return fallback


def create_database(path: Path, force: bool) -> sqlite3.Connection:
    if path.exists():
        if not force:
            raise FileExistsError(f"Database already exists: {path}. Pass --force to replace it.")
        path.unlink()
    path.parent.mkdir(parents=True, exist_ok=True)
    connection = sqlite3.connect(path)
    connection.executescript(
        """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        PRAGMA temp_store = MEMORY;

        CREATE TABLE metadata (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE works (
            title TEXT PRIMARY KEY,
            he_title TEXT NOT NULL,
            categories_json TEXT NOT NULL,
            schema_json TEXT NOT NULL,
            alt_structs_json TEXT NOT NULL,
            dependence TEXT NOT NULL,
            collective_title TEXT NOT NULL,
            authors_json TEXT NOT NULL,
            en_description TEXT NOT NULL,
            he_description TEXT NOT NULL,
            en_short_description TEXT NOT NULL,
            he_short_description TEXT NOT NULL
        );

        CREATE TABLE terms (
            name TEXT PRIMARY KEY,
            en_title TEXT NOT NULL,
            he_title TEXT NOT NULL,
            titles_json TEXT NOT NULL
        );

        CREATE TABLE versions (
            id INTEGER PRIMARY KEY,
            title TEXT NOT NULL REFERENCES works(title),
            language TEXT NOT NULL,
            actual_language TEXT NOT NULL,
            language_family TEXT NOT NULL,
            version_title TEXT NOT NULL,
            version_title_hebrew TEXT NOT NULL,
            version_source TEXT NOT NULL,
            license TEXT NOT NULL,
            status TEXT NOT NULL,
            direction TEXT NOT NULL,
            is_primary INTEGER NOT NULL,
            is_source INTEGER NOT NULL,
            text_shape TEXT NOT NULL,
            segment_count INTEGER NOT NULL,
            character_count INTEGER NOT NULL,
            text_sha256 TEXT NOT NULL,
            text_json TEXT NOT NULL,
            UNIQUE(title, language, version_title)
        );

        CREATE INDEX versions_by_title ON versions(title, actual_language);

        CREATE TABLE segments (
            version_id INTEGER NOT NULL REFERENCES versions(id),
            path TEXT NOT NULL,
            text TEXT NOT NULL,
            PRIMARY KEY(version_id, path)
        ) WITHOUT ROWID;

        CREATE TABLE links (
            id INTEGER PRIMARY KEY,
            bson_id TEXT NOT NULL UNIQUE,
            type TEXT NOT NULL,
            refs_json TEXT NOT NULL,
            anchor_text TEXT NOT NULL,
            is_auto INTEGER NOT NULL,
            generated_by TEXT NOT NULL,
            available_languages_json TEXT NOT NULL
        );

        CREATE TABLE link_endpoints (
            anchor_ref TEXT NOT NULL,
            link_id INTEGER NOT NULL REFERENCES links(id),
            side INTEGER NOT NULL,
            other_ref TEXT NOT NULL,
            other_index_title TEXT NOT NULL,
            category TEXT NOT NULL,
            collective_title_en TEXT NOT NULL,
            collective_title_he TEXT NOT NULL,
            source_has_english INTEGER NOT NULL,
            PRIMARY KEY(anchor_ref, link_id, side)
        ) WITHOUT ROWID;

        CREATE INDEX link_endpoints_by_anchor ON link_endpoints(anchor_ref);
        """
    )
    connection.executemany(
        "INSERT INTO metadata(key, value) VALUES (?, ?)",
        [("schema_version", SCHEMA_VERSION), ("created_utc", time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()))],
    )
    return connection


def load_manifest(path: Path) -> tuple[list[dict[str, str]], list[str]]:
    data = json.loads(path.read_text(encoding="utf-8"))
    works = data.get("works", [])
    references = data.get("link_references", [])
    if not works or not all(isinstance(item.get("title"), str) for item in works):
        raise ValueError("The sample manifest must contain work title entries.")
    if not references or not all(isinstance(item, str) for item in references):
        raise ValueError("The sample manifest must contain link references.")
    return works, references


def import_terms(connection: sqlite3.Connection, path: Path) -> dict[str, tuple[str, str]]:
    rows: list[tuple[str, str, str, str]] = []
    lookup: dict[str, tuple[str, str]] = {}
    for document in bson_documents(path):
        name = str(document.get("name") or "")
        titles = document.get("titles") if isinstance(document.get("titles"), list) else []
        en_title = ""
        he_title = ""
        for title in titles:
            if not isinstance(title, dict):
                continue
            if title.get("lang") == "en" and (title.get("primary") or not en_title):
                en_title = str(title.get("text") or "")
            if title.get("lang") == "he" and (title.get("primary") or not he_title):
                he_title = str(title.get("text") or "")
        rows.append((name, en_title, he_title, compact_json(titles)))
        lookup[name] = (en_title, he_title)
    connection.executemany("INSERT INTO terms VALUES (?, ?, ?, ?)", rows)
    return lookup


def import_works(connection: sqlite3.Connection, path: Path) -> dict[str, dict[str, Any]]:
    lookup: dict[str, dict[str, Any]] = {}
    rows = []
    for document in bson_documents(path):
        title = str(document.get("title") or "")
        schema = document.get("schema") if isinstance(document.get("schema"), dict) else {}
        categories = document.get("categories") if isinstance(document.get("categories"), list) else []
        row = (
            title,
            primary_title(schema, "he"),
            compact_json(categories),
            compact_json(schema),
            compact_json(document.get("alt_structs") or {}),
            str(document.get("dependence") or ""),
            str(document.get("collective_title") or ""),
            compact_json(document.get("authors") or []),
            str(document.get("enDesc") or ""),
            str(document.get("heDesc") or ""),
            str(document.get("enShortDesc") or ""),
            str(document.get("heShortDesc") or ""),
        )
        rows.append(row)
        lookup[title] = {
            "title": title,
            "he_title": row[1],
            "categories": categories,
            "dependence": row[5],
            "collective_title": row[6],
        }
    connection.executemany("INSERT INTO works VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)", rows)
    return lookup


def version_rank(document: dict[str, Any], stats: TextStats) -> tuple[int, int, float, int, int]:
    priority = document.get("priority")
    try:
        numeric_priority = float(priority)
    except (TypeError, ValueError):
        numeric_priority = 0.0
    return (
        int(stats.segments > 0),
        int(bool(document.get("isPrimary"))),
        numeric_priority,
        int(document.get("status") == "locked"),
        stats.characters,
    )


def choose_sample_versions(path: Path, requested_titles: set[str]) -> tuple[dict[str, list[tuple[dict[str, Any], TextStats]]], Counter[str]]:
    candidates: dict[tuple[str, str], list[tuple[dict[str, Any], TextStats]]] = defaultdict(list)
    raw_version_counts: Counter[str] = Counter()
    for document in bson_documents(path):
        title = str(document.get("title") or "")
        if title not in requested_titles:
            continue
        stats = text_stats(document.get("chapter"))
        raw_version_counts[title] += 1
        language = str(document.get("actualLanguage") or document.get("language") or "unknown")
        candidates[(title, language)].append((document, stats))

    chosen: dict[str, list[tuple[dict[str, Any], TextStats]]] = defaultdict(list)
    for (title, language), versions in candidates.items():
        if language not in {"he", "en"} and any(key[0] == title and key[1] in {"he", "en"} for key in candidates):
            continue
        chosen[title].append(max(versions, key=lambda item: version_rank(item[0], item[1])))
    return chosen, raw_version_counts


def import_versions(
    connection: sqlite3.Connection,
    texts_path: Path,
    requested_titles: set[str],
) -> tuple[dict[str, list[dict[str, Any]]], Counter[str]]:
    chosen, raw_version_counts = choose_sample_versions(texts_path, requested_titles)
    imported: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for title in sorted(chosen):
        for document, stats in sorted(chosen[title], key=lambda item: str(item[0].get("actualLanguage") or "")):
            text = document.get("chapter")
            row = (
                title,
                str(document.get("language") or ""),
                str(document.get("actualLanguage") or document.get("language") or ""),
                str(document.get("languageFamilyName") or ""),
                str(document.get("versionTitle") or ""),
                str(document.get("versionTitleInHebrew") or ""),
                str(document.get("versionSource") or ""),
                str(document.get("license") or ""),
                str(document.get("status") or ""),
                str(document.get("direction") or ""),
                int(bool(document.get("isPrimary"))),
                int(bool(document.get("isSource"))),
                stats.shape,
                stats.segments,
                stats.characters,
                json_hash(text),
                compact_json(text),
            )
            cursor = connection.execute(
                """
                INSERT INTO versions(
                    title, language, actual_language, language_family, version_title,
                    version_title_hebrew, version_source, license, status, direction,
                    is_primary, is_source, text_shape, segment_count, character_count,
                    text_sha256, text_json
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                """,
                row,
            )
            version_id = int(cursor.lastrowid)
            segment_rows = ((version_id, "/".join(path), value) for path, value in walk_text(text))
            connection.executemany("INSERT INTO segments(version_id, path, text) VALUES (?, ?, ?)", segment_rows)
            imported[title].append(
                {
                    "version_id": version_id,
                    "actual_language": row[2],
                    "version_title": row[4],
                    "license": row[7],
                    "shape": stats.shape,
                    "segments": stats.segments,
                    "characters": stats.characters,
                }
            )
    return imported, raw_version_counts


def title_candidates_by_first_word(works: dict[str, dict[str, Any]]) -> dict[str, list[str]]:
    candidates: dict[str, list[str]] = defaultdict(list)
    for title in works:
        first_word = title.split(" ", 1)[0]
        candidates[first_word].append(title)
    for titles in candidates.values():
        titles.sort(key=len, reverse=True)
    return candidates


def resolve_work_title(reference: str, candidates: dict[str, list[str]]) -> str:
    first_word = reference.split(" ", 1)[0].rstrip(",")
    for title in candidates.get(first_word, []):
        if reference == title or reference.startswith(title + " ") or reference.startswith(title + ","):
            return title
    return ""


def import_sample_links(
    connection: sqlite3.Connection,
    path: Path,
    requested_references: set[str],
    works: dict[str, dict[str, Any]],
    terms: dict[str, tuple[str, str]],
) -> dict[str, int]:
    counts: Counter[str] = Counter()
    title_candidates = title_candidates_by_first_word(works)
    for document in bson_documents(path):
        expanded = [document.get("expandedRefs0") or [], document.get("expandedRefs1") or []]
        matching_sides: list[tuple[str, int]] = []
        for side in (0, 1):
            if not isinstance(expanded[side], list):
                continue
            matching_sides.extend((reference, side) for reference in requested_references.intersection(expanded[side]))
        if not matching_sides:
            continue
        refs = document.get("refs") if isinstance(document.get("refs"), list) else []
        cursor = connection.execute(
            """
            INSERT INTO links(bson_id, type, refs_json, anchor_text, is_auto, generated_by, available_languages_json)
            VALUES (?, ?, ?, ?, ?, ?, ?)
            """,
            (
                str(document.get("_id") or ""),
                str(document.get("type") or "Other"),
                compact_json(refs),
                str(document.get("anchorText") or ""),
                int(bool(document.get("auto"))),
                str(document.get("generated_by") or ""),
                compact_json(document.get("availableLangs") or []),
            ),
        )
        link_id = int(cursor.lastrowid)
        for reference, side in matching_sides:
            other_ref = str(refs[1 - side]) if len(refs) > 1 else ""
            index_title = resolve_work_title(other_ref, title_candidates)
            work = works.get(index_title, {})
            dependence = str(work.get("dependence") or "")
            categories = work.get("categories") or []
            category = "Commentary" if dependence.lower() == "commentary" else str(categories[0] if categories else "Other")
            collective_key = str(work.get("collective_title") or "")
            collective_en, collective_he = terms.get(collective_key, (collective_key, ""))
            available = document.get("availableLangs") or []
            other_languages = available[1 - side] if isinstance(available, list) and len(available) > 1 else []
            connection.execute(
                """
                INSERT INTO link_endpoints(
                    anchor_ref, link_id, side, other_ref, other_index_title, category,
                    collective_title_en, collective_title_he, source_has_english
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
                """,
                (
                    reference,
                    link_id,
                    side,
                    other_ref,
                    index_title,
                    category,
                    collective_en,
                    collective_he,
                    int("en" in other_languages),
                ),
            )
            counts[reference] += 1
    return dict(counts)


def derive_link_items(
    connection: sqlite3.Connection,
    anchor_ref: str,
) -> list[dict[str, Any]]:
    rows = connection.execute(
        """
        SELECT e.other_ref, l.type, e.other_index_title, e.category,
               e.collective_title_en, e.collective_title_he, e.source_has_english
        FROM link_endpoints e
        JOIN links l ON l.id = e.link_id
        WHERE e.anchor_ref = ?
        ORDER BY e.other_ref
        """,
        (anchor_ref,),
    ).fetchall()
    items = []
    for other_ref, link_type, index_title, category, collective_en, collective_he, source_has_english in rows:
        items.append(
            {
                "ref": other_ref,
                "anchorRef": anchor_ref,
                "sourceRef": other_ref,
                "sourceHeRef": "",
                "indexTitle": index_title,
                "collectiveTitleEnglish": collective_en,
                "collectiveTitleHebrew": collective_he,
                "category": category,
                "type": link_type or "Other",
                "sourceHasEnglish": bool(source_has_english),
                "anchorVerse": 0,
            }
        )
    return items


def verify_database(
    connection: sqlite3.Connection,
    requested_works: list[dict[str, str]],
    requested_references: list[str],
) -> dict[str, Any]:
    expected_titles = {item["title"] for item in requested_works}
    imported_titles = {row[0] for row in connection.execute("SELECT DISTINCT title FROM versions")}
    missing_titles = sorted(expected_titles - imported_titles)
    version_rows = connection.execute(
        "SELECT id, title, actual_language, version_title, license, text_shape, segment_count, character_count, text_sha256, text_json FROM versions"
    ).fetchall()
    hash_failures = []
    segment_failures = []
    licenses: Counter[str] = Counter()
    shapes: Counter[str] = Counter()
    for version_id, title, language, version_title, license_name, shape, segment_count, character_count, digest, text_json in version_rows:
        value = json.loads(text_json)
        actual_stats = text_stats(value)
        if json_hash(value) != digest:
            hash_failures.append(f"{title}|{language}|{version_title}")
        stored_segments = connection.execute("SELECT COUNT(*) FROM segments WHERE version_id = ?", (version_id,)).fetchone()[0]
        if stored_segments != segment_count or actual_stats != TextStats(segment_count, character_count, shape):
            segment_failures.append(f"{title}|{language}|{version_title}")
        licenses[license_name or "(missing)"] += 1
        shapes[shape] += 1

    anchor_counts = {
        reference: connection.execute("SELECT COUNT(*) FROM link_endpoints WHERE anchor_ref = ?", (reference,)).fetchone()[0]
        for reference in requested_references
    }
    compatibility = Counter()
    query_times_ms: list[float] = []
    visible_examples: dict[str, list[dict[str, Any]]] = {}
    for reference in requested_references:
        started = time.perf_counter()
        items = derive_link_items(connection, reference)
        elapsed_ms = (time.perf_counter() - started) * 1000
        query_times_ms.append(elapsed_ms)
        compatibility["items"] += len(items)
        compatibility["resolved_index_title"] += sum(bool(item["indexTitle"]) for item in items)
        compatibility["resolved_category"] += sum(item["category"] != "Other" for item in items)
        compatibility["commentary"] += sum(item["category"] == "Commentary" for item in items)
        compatibility["visible_links"] += sum(item["category"] != "Commentary" for item in items)
        compatibility["queries_under_50ms"] += int(elapsed_ms < 50)
        if items:
            visible_examples[reference] = items[:3]

    problems = missing_titles + hash_failures + segment_failures
    return {
        "passed": not problems and bool(version_rows) and compatibility["items"] > 0,
        "sample_works_requested": len(expected_titles),
        "sample_works_imported": len(imported_titles),
        "missing_sample_works": missing_titles,
        "versions_imported": len(version_rows),
        "segments_imported": connection.execute("SELECT COUNT(*) FROM segments").fetchone()[0],
        "text_hash_failures": hash_failures,
        "segment_validation_failures": segment_failures,
        "version_licenses": dict(licenses),
        "text_shapes": dict(shapes),
        "link_references_requested": len(requested_references),
        "link_references_with_results": sum(count > 0 for count in anchor_counts.values()),
        "link_counts": anchor_counts,
        "link_compatibility": dict(compatibility),
        "link_query_ms": {
            "minimum": round(min(query_times_ms, default=0.0), 3),
            "median": round(sorted(query_times_ms)[len(query_times_ms) // 2], 3) if query_times_ms else 0.0,
            "maximum": round(max(query_times_ms, default=0.0), 3),
        },
        "link_examples": visible_examples,
    }


def load_work_lookup(connection: sqlite3.Connection) -> dict[str, dict[str, Any]]:
    result = {}
    for title, he_title, categories_json, dependence, collective_title in connection.execute(
        "SELECT title, he_title, categories_json, dependence, collective_title FROM works"
    ):
        result[title] = {
            "title": title,
            "he_title": he_title,
            "categories": json.loads(categories_json),
            "dependence": dependence,
            "collective_title": collective_title,
        }
    return result


def load_term_lookup(connection: sqlite3.Connection) -> dict[str, tuple[str, str]]:
    return {name: (en_title, he_title) for name, en_title, he_title in connection.execute("SELECT name, en_title, he_title FROM terms")}


def build(args: argparse.Namespace) -> int:
    dump_dir = args.dump_dir.resolve()
    manifest_works, references = load_manifest(args.manifest)
    requested_titles = {item["title"] for item in manifest_works}
    started = time.perf_counter()
    connection = create_database(args.database, args.force)
    try:
        terms = import_terms(connection, require_collection(dump_dir, "term"))
        works = import_works(connection, require_collection(dump_dir, "index"))
        missing_index = sorted(requested_titles - works.keys())
        if missing_index:
            raise ValueError(f"Sample works missing from index: {', '.join(missing_index)}")
        imported_versions, raw_counts = import_versions(connection, require_collection(dump_dir, "texts"), requested_titles)
        import_sample_links(connection, require_collection(dump_dir, "links"), set(references), works, terms)
        connection.executemany(
            "INSERT INTO metadata(key, value) VALUES (?, ?)",
            [
                ("dump_directory", str(dump_dir)),
                ("sample_manifest", str(args.manifest.resolve())),
                ("sample_raw_version_counts", compact_json(raw_counts)),
                ("sample_imported_versions", compact_json(imported_versions)),
            ],
        )
        connection.commit()
        report = verify_database(connection, manifest_works, references)
        report["build_seconds"] = round(time.perf_counter() - started, 3)
        connection.execute("PRAGMA wal_checkpoint(TRUNCATE)")
        report["database_bytes"] = args.database.stat().st_size
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print(json.dumps({key: value for key, value in report.items() if key != "link_examples"}, ensure_ascii=False, indent=2))
        print(f"Report: {args.report}")
        print(f"Database: {args.database}")
        return 0 if report["passed"] else 1
    finally:
        connection.close()


def verify(args: argparse.Namespace) -> int:
    manifest_works, references = load_manifest(args.manifest)
    connection = sqlite3.connect(args.database)
    try:
        report = verify_database(connection, manifest_works, references)
        print(json.dumps(report, ensure_ascii=False, indent=2))
        return 0 if report["passed"] else 1
    finally:
        connection.close()


def query_links(args: argparse.Namespace) -> int:
    connection = sqlite3.connect(args.database)
    try:
        items = derive_link_items(connection, args.reference)
        if not args.include_commentary:
            items = [item for item in items if item["category"] != "Commentary"]
        print(json.dumps(items, ensure_ascii=False, indent=2))
        return 0
    finally:
        connection.close()


def parser() -> argparse.ArgumentParser:
    default_manifest = Path(__file__).with_name("sefaria_dump_samples.json")
    result = argparse.ArgumentParser(description=__doc__)
    subparsers = result.add_subparsers(dest="command", required=True)

    build_parser = subparsers.add_parser("build", help="Build and verify the prototype database")
    build_parser.add_argument("--dump-dir", type=Path, required=True, help="Folder containing texts.bson, index.bson, and links.bson")
    build_parser.add_argument("--database", type=Path, default=Path("artifacts/sefaria-prototype.sqlite"))
    build_parser.add_argument("--report", type=Path, default=Path("artifacts/sefaria-prototype-report.json"))
    build_parser.add_argument("--manifest", type=Path, default=default_manifest)
    build_parser.add_argument("--force", action="store_true", help="Replace an existing prototype database")
    build_parser.set_defaults(handler=build)

    verify_parser = subparsers.add_parser("verify", help="Re-run integrity and compatibility checks")
    verify_parser.add_argument("--database", type=Path, default=Path("artifacts/sefaria-prototype.sqlite"))
    verify_parser.add_argument("--manifest", type=Path, default=default_manifest)
    verify_parser.set_defaults(handler=verify)

    query_parser = subparsers.add_parser("query-links", help="Return Stndr-shaped links for one imported reference")
    query_parser.add_argument("reference")
    query_parser.add_argument("--database", type=Path, default=Path("artifacts/sefaria-prototype.sqlite"))
    query_parser.add_argument("--include-commentary", action="store_true")
    query_parser.set_defaults(handler=query_links)
    return result


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    args = parser().parse_args()
    return int(args.handler(args))


if __name__ == "__main__":
    sys.exit(main())
