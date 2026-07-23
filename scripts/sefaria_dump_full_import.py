#!/usr/bin/env python3
"""Build and verify an optimized full-corpus SQLite database from Sefaria BSON.

This phase-one engineering prototype imports every work, version, and link from
Sefaria's small MongoDB dump. Text content is stored once as zlib-compressed
compact JSON. Link endpoints use normalized integer reference IDs so expanded
ranges do not repeat reference strings millions of times.

The database is an experiment for sizing and query design. It is not yet the
runtime database used by Stndr.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import sqlite3
import sys
import time
import zlib
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any, Iterable, Iterator

from sefaria_dump_prototype import (
    bson_documents,
    compact_json,
    load_manifest,
    primary_title,
    require_collection,
    text_stats,
)


SCHEMA_VERSION = "1"
LINK_BATCH_SIZE = 20_000
TEXT_COMPRESSION_LEVEL = 6


def utc_now() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


def mib(value: int) -> float:
    return round(value / (1024 * 1024), 3)


def gib(value: int) -> float:
    return round(value / (1024 * 1024 * 1024), 3)


def print_progress(message: str) -> None:
    print(f"[{time.strftime('%H:%M:%S')}] {message}", flush=True)


def allocated_database_bytes(connection: sqlite3.Connection) -> int:
    page_count = int(connection.execute("PRAGMA page_count").fetchone()[0])
    page_size = int(connection.execute("PRAGMA page_size").fetchone()[0])
    return page_count * page_size


def json_without_id(document: dict[str, Any]) -> str:
    return compact_json({key: value for key, value in document.items() if key != "_id"})


def language_mask(languages: Any) -> int:
    if not isinstance(languages, list):
        return 0
    mask = 0
    for language in languages:
        normalized = str(language or "").lower()
        if normalized == "en":
            mask |= 1
        elif normalized == "he":
            mask |= 2
        elif normalized:
            mask |= 4
    return mask


def create_database(path: Path, force: bool) -> sqlite3.Connection:
    if path.exists():
        if not force:
            raise FileExistsError(f"Database already exists: {path}. Pass --force to replace it.")
        path.unlink()
    for suffix in ("-journal", "-wal", "-shm"):
        companion = Path(str(path) + suffix)
        if companion.exists():
            companion.unlink()
    path.parent.mkdir(parents=True, exist_ok=True)
    connection = sqlite3.connect(path)
    connection.executescript(
        """
        PRAGMA page_size = 8192;
        PRAGMA journal_mode = OFF;
        PRAGMA synchronous = OFF;
        PRAGMA locking_mode = EXCLUSIVE;
        PRAGMA temp_store = MEMORY;
        PRAGMA cache_size = -1048576;
        PRAGMA foreign_keys = OFF;

        CREATE TABLE metadata (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        ) WITHOUT ROWID;

        CREATE TABLE works (
            id INTEGER PRIMARY KEY,
            title TEXT NOT NULL UNIQUE,
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
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL UNIQUE,
            en_title TEXT NOT NULL,
            he_title TEXT NOT NULL,
            titles_json TEXT NOT NULL
        );

        CREATE TABLE categories (
            id INTEGER PRIMARY KEY,
            path_json TEXT NOT NULL,
            last_path TEXT NOT NULL,
            depth INTEGER NOT NULL,
            shared_title TEXT NOT NULL,
            data_json TEXT NOT NULL
        );

        CREATE TABLE people (
            id INTEGER PRIMARY KEY,
            person_key TEXT NOT NULL,
            names_json TEXT NOT NULL,
            data_json TEXT NOT NULL
        );

        CREATE TABLE versions (
            id INTEGER PRIMARY KEY,
            work_id INTEGER NOT NULL REFERENCES works(id),
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
            priority REAL NOT NULL,
            text_shape TEXT NOT NULL,
            segment_count INTEGER NOT NULL,
            character_count INTEGER NOT NULL,
            uncompressed_bytes INTEGER NOT NULL,
            compressed_bytes INTEGER NOT NULL,
            text_sha256 BLOB NOT NULL,
            content_zlib BLOB NOT NULL,
            metadata_json TEXT NOT NULL,
            UNIQUE(work_id, language, version_title)
        );

        CREATE TABLE refs (
            id INTEGER PRIMARY KEY,
            reference TEXT NOT NULL UNIQUE
        );

        CREATE TABLE links (
            id INTEGER PRIMARY KEY,
            ref0_id INTEGER NOT NULL REFERENCES refs(id),
            ref1_id INTEGER NOT NULL REFERENCES refs(id),
            work0_id INTEGER REFERENCES works(id),
            work1_id INTEGER REFERENCES works(id),
            link_type TEXT NOT NULL,
            anchor_text TEXT NOT NULL,
            is_auto INTEGER NOT NULL,
            generated_by TEXT NOT NULL,
            available0 INTEGER NOT NULL,
            available1 INTEGER NOT NULL,
            inline_citation INTEGER NOT NULL,
            extra_json TEXT NOT NULL
        );

        CREATE TABLE link_endpoints (
            ref_id INTEGER NOT NULL REFERENCES refs(id),
            link_id INTEGER NOT NULL REFERENCES links(id),
            side INTEGER NOT NULL,
            PRIMARY KEY(ref_id, link_id, side)
        ) WITHOUT ROWID;
        """
    )
    connection.executemany(
        "INSERT INTO metadata(key, value) VALUES (?, ?)",
        [("schema_version", SCHEMA_VERSION), ("created_utc", utc_now())],
    )
    return connection


def import_small_metadata(
    connection: sqlite3.Connection,
    dump_dir: Path,
) -> tuple[dict[str, int], dict[str, dict[str, Any]], dict[str, tuple[str, str]], dict[str, int]]:
    started = time.perf_counter()
    term_lookup: dict[str, tuple[str, str]] = {}
    term_rows = []
    for term_id, document in enumerate(bson_documents(require_collection(dump_dir, "term")), start=1):
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
        term_lookup[name] = (en_title, he_title)
        term_rows.append((term_id, name, en_title, he_title, compact_json(titles)))
    connection.executemany("INSERT INTO terms VALUES (?, ?, ?, ?, ?)", term_rows)

    work_ids: dict[str, int] = {}
    works: dict[str, dict[str, Any]] = {}
    work_rows = []
    for work_id, document in enumerate(bson_documents(require_collection(dump_dir, "index")), start=1):
        title = str(document.get("title") or "")
        schema = document.get("schema") if isinstance(document.get("schema"), dict) else {}
        categories = document.get("categories") if isinstance(document.get("categories"), list) else []
        work_ids[title] = work_id
        works[title] = {
            "id": work_id,
            "title": title,
            "categories": categories,
            "dependence": str(document.get("dependence") or ""),
            "collective_title": str(document.get("collective_title") or ""),
        }
        work_rows.append(
            (
                work_id,
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
        )
    connection.executemany("INSERT INTO works VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)", work_rows)

    category_rows = []
    for category_id, document in enumerate(bson_documents(require_collection(dump_dir, "category")), start=1):
        category_rows.append(
            (
                category_id,
                compact_json(document.get("path") or []),
                str(document.get("lastPath") or ""),
                int(document.get("depth") or 0),
                str(document.get("sharedTitle") or ""),
                json_without_id(document),
            )
        )
    connection.executemany("INSERT INTO categories VALUES (?, ?, ?, ?, ?, ?)", category_rows)

    person_rows = []
    person_ids: dict[str, int] = {}
    for person_id, document in enumerate(bson_documents(require_collection(dump_dir, "person")), start=1):
        key = str(document.get("key") or "")
        person_ids[key] = person_id
        person_rows.append(
            (
                person_id,
                key,
                compact_json(document.get("names") or []),
                json_without_id(document),
            )
        )
    connection.executemany("INSERT INTO people VALUES (?, ?, ?, ?)", person_rows)
    connection.commit()
    print_progress(
        f"Metadata: {len(work_rows):,} works, {len(term_rows):,} terms, "
        f"{len(category_rows):,} categories, {len(person_rows):,} people "
        f"in {time.perf_counter() - started:.1f}s"
    )
    return work_ids, works, term_lookup, person_ids


def selected_version_metadata(document: dict[str, Any]) -> dict[str, Any]:
    excluded = {"_id", "chapter"}
    return {key: value for key, value in document.items() if key not in excluded}


def import_all_versions(
    connection: sqlite3.Connection,
    path: Path,
    work_ids: dict[str, int],
    works: dict[str, dict[str, Any]],
) -> dict[str, Any]:
    started = time.perf_counter()
    bytes_before = allocated_database_bytes(connection)
    count = 0
    synthetic_works = []
    raw_bytes = 0
    compressed_bytes = 0
    segments = 0
    characters = 0
    languages: Counter[str] = Counter()
    licenses: Counter[str] = Counter()
    shapes: Counter[str] = Counter()
    rows = []
    for document in bson_documents(path):
        title = str(document.get("title") or "")
        work_id = work_ids.get(title)
        if work_id is None:
            work_id = len(work_ids) + 1
            work_ids[title] = work_id
            works[title] = {
                "id": work_id,
                "title": title,
                "categories": [],
                "dependence": "",
                "collective_title": "",
            }
            connection.execute(
                "INSERT INTO works VALUES (?, ?, '', '[]', '{}', '{}', '', '', '[]', '', '', '', '')",
                (work_id, title),
            )
            synthetic_works.append(title)
        content = document.get("chapter")
        stats = text_stats(content)
        content_bytes = compact_json(content).encode("utf-8")
        compressed = zlib.compress(content_bytes, level=TEXT_COMPRESSION_LEVEL)
        digest = hashlib.sha256(content_bytes).digest()
        try:
            priority = float(document.get("priority") or 0.0)
        except (TypeError, ValueError):
            priority = 0.0
        count += 1
        raw_bytes += len(content_bytes)
        compressed_bytes += len(compressed)
        segments += stats.segments
        characters += stats.characters
        actual_language = str(document.get("actualLanguage") or document.get("language") or "")
        license_name = str(document.get("license") or "")
        languages[actual_language or "(missing)"] += 1
        licenses[license_name or "(missing)"] += 1
        shapes[stats.shape] += 1
        rows.append(
            (
                count,
                work_id,
                str(document.get("language") or ""),
                actual_language,
                str(document.get("languageFamilyName") or ""),
                str(document.get("versionTitle") or ""),
                str(document.get("versionTitleInHebrew") or ""),
                str(document.get("versionSource") or ""),
                license_name,
                str(document.get("status") or ""),
                str(document.get("direction") or ""),
                int(bool(document.get("isPrimary"))),
                int(bool(document.get("isSource"))),
                priority,
                stats.shape,
                stats.segments,
                stats.characters,
                len(content_bytes),
                len(compressed),
                digest,
                compressed,
                compact_json(selected_version_metadata(document)),
            )
        )
        if len(rows) >= 100:
            connection.executemany(
                "INSERT INTO versions VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                rows,
            )
            rows.clear()
        if count % 1000 == 0:
            connection.commit()
            print_progress(
                f"Texts: {count:,} versions, {segments:,} segments, "
                f"{gib(compressed_bytes):.2f} GiB compressed"
            )
    if rows:
        connection.executemany(
            "INSERT INTO versions VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
            rows,
        )
    connection.commit()
    elapsed = time.perf_counter() - started
    bytes_after = allocated_database_bytes(connection)
    print_progress(
        f"Texts complete: {count:,} versions and {segments:,} segments in {elapsed:.1f}s; "
        f"{gib(raw_bytes):.2f} -> {gib(compressed_bytes):.2f} GiB"
    )
    return {
        "seconds": round(elapsed, 3),
        "versions": count,
        "synthetic_works": synthetic_works,
        "segments": segments,
        "characters": characters,
        "uncompressed_bytes": raw_bytes,
        "compressed_bytes": compressed_bytes,
        "compression_ratio": round(compressed_bytes / raw_bytes, 5) if raw_bytes else 0,
        "database_bytes_before": bytes_before,
        "database_bytes_after": bytes_after,
        "database_bytes_added": bytes_after - bytes_before,
        "languages": dict(languages),
        "licenses": dict(licenses),
        "shapes": dict(shapes),
    }


def title_candidates_by_first_word(works: dict[str, dict[str, Any]]) -> dict[str, list[str]]:
    result: dict[str, list[str]] = defaultdict(list)
    for title in works:
        result[title.split(" ", 1)[0]].append(title)
    for candidates in result.values():
        candidates.sort(key=len, reverse=True)
    return result


def resolve_work_id(reference: str, candidates: dict[str, list[str]], works: dict[str, dict[str, Any]]) -> int | None:
    first_word = reference.split(" ", 1)[0].rstrip(",")
    for title in candidates.get(first_word, []):
        if reference == title or reference.startswith(title + " ") or reference.startswith(title + ","):
            return int(works[title]["id"])
    return None


def extra_link_metadata(document: dict[str, Any]) -> str:
    retained = {}
    for key in ("inline_reference", "charLevelData", "score", "versions", "displayedText"):
        if key in document:
            retained[key] = document[key]
    return compact_json(retained) if retained else ""


def import_all_links(
    connection: sqlite3.Connection,
    path: Path,
    works: dict[str, dict[str, Any]],
) -> dict[str, Any]:
    started = time.perf_counter()
    bytes_before = allocated_database_bytes(connection)
    candidates = title_candidates_by_first_word(works)
    ref_ids: dict[str, int] = {}
    new_ref_rows: list[tuple[int, str]] = []
    link_rows: list[tuple[Any, ...]] = []
    endpoint_rows: list[tuple[int, int, int]] = []
    next_ref_id = 1
    link_id = 0
    source_documents = 0
    malformed = 0
    expected_endpoints = 0
    unresolved_sides = 0
    link_types: Counter[str] = Counter()
    max_expansion = 0

    def get_ref_id(reference: Any) -> int:
        nonlocal next_ref_id
        normalized = str(reference or "").strip()
        existing = ref_ids.get(normalized)
        if existing is not None:
            return existing
        assigned = next_ref_id
        next_ref_id += 1
        ref_ids[normalized] = assigned
        new_ref_rows.append((assigned, normalized))
        return assigned

    def flush() -> None:
        if new_ref_rows:
            connection.executemany("INSERT INTO refs(id, reference) VALUES (?, ?)", new_ref_rows)
            new_ref_rows.clear()
        if link_rows:
            connection.executemany(
                "INSERT INTO links VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                link_rows,
            )
            link_rows.clear()
        if endpoint_rows:
            connection.executemany(
                "INSERT OR IGNORE INTO link_endpoints(ref_id, link_id, side) VALUES (?, ?, ?)",
                endpoint_rows,
            )
            endpoint_rows.clear()

    for document in bson_documents(path):
        source_documents += 1
        refs = document.get("refs") if isinstance(document.get("refs"), list) else []
        if len(refs) < 2:
            malformed += 1
            continue
        ref0 = str(refs[0] or "")
        ref1 = str(refs[1] or "")
        ref0_id = get_ref_id(ref0)
        ref1_id = get_ref_id(ref1)
        work0_id = resolve_work_id(ref0, candidates, works)
        work1_id = resolve_work_id(ref1, candidates, works)
        unresolved_sides += int(work0_id is None) + int(work1_id is None)
        available = document.get("availableLangs") if isinstance(document.get("availableLangs"), list) else []
        available0 = language_mask(available[0] if len(available) > 0 else [])
        available1 = language_mask(available[1] if len(available) > 1 else [])
        link_id += 1
        link_type = str(document.get("type") or "Other")
        link_types[link_type] += 1
        link_rows.append(
            (
                link_id,
                ref0_id,
                ref1_id,
                work0_id,
                work1_id,
                link_type,
                str(document.get("anchorText") or ""),
                int(bool(document.get("auto"))),
                str(document.get("generated_by") or ""),
                available0,
                available1,
                int(bool(document.get("inline_citation"))),
                extra_link_metadata(document),
            )
        )
        expansion_count = 0
        for side, key, fallback_ref_id in ((0, "expandedRefs0", ref0_id), (1, "expandedRefs1", ref1_id)):
            expanded = document.get(key)
            if isinstance(expanded, list) and expanded:
                unique_ids = {get_ref_id(reference) for reference in expanded}
            else:
                unique_ids = {fallback_ref_id}
            expansion_count += len(unique_ids)
            endpoint_rows.extend((expanded_ref_id, link_id, side) for expanded_ref_id in unique_ids)
        expected_endpoints += expansion_count
        max_expansion = max(max_expansion, expansion_count)

        if link_id % LINK_BATCH_SIZE == 0:
            flush()
            connection.commit()
        if link_id % 250_000 == 0:
            print_progress(
                f"Links: {link_id:,} logical, {expected_endpoints:,} endpoints, "
                f"{len(ref_ids):,} unique refs, DB {gib(connection.execute('PRAGMA page_count').fetchone()[0] * 8192):.2f} GiB"
            )

    flush()
    connection.commit()
    actual_endpoints = int(connection.execute("SELECT COUNT(*) FROM link_endpoints").fetchone()[0])
    elapsed = time.perf_counter() - started
    bytes_after = allocated_database_bytes(connection)
    print_progress(
        f"Links complete: {link_id:,} links, {actual_endpoints:,} endpoints, "
        f"{len(ref_ids):,} refs in {elapsed:.1f}s"
    )
    return {
        "seconds": round(elapsed, 3),
        "source_documents": source_documents,
        "links": link_id,
        "malformed_documents": malformed,
        "unique_references": len(ref_ids),
        "expected_unique_endpoints": expected_endpoints,
        "stored_endpoints": actual_endpoints,
        "max_expansion": max_expansion,
        "unresolved_link_sides": unresolved_sides,
        "link_types": dict(link_types),
        "database_bytes_before": bytes_before,
        "database_bytes_after": bytes_after,
        "database_bytes_added": bytes_after - bytes_before,
    }


def create_final_indexes(connection: sqlite3.Connection) -> dict[str, Any]:
    started = time.perf_counter()
    before = connection.execute("PRAGMA page_count").fetchone()[0] * connection.execute("PRAGMA page_size").fetchone()[0]
    print_progress("Creating final indexes")
    connection.executescript(
        """
        CREATE INDEX versions_by_work_language ON versions(work_id, actual_language, is_primary DESC, priority DESC);
        CREATE INDEX people_by_key ON people(person_key);
        CREATE INDEX links_by_ref0 ON links(ref0_id);
        CREATE INDEX links_by_ref1 ON links(ref1_id);
        CREATE INDEX links_by_work0 ON links(work0_id);
        CREATE INDEX links_by_work1 ON links(work1_id);
        ANALYZE;
        PRAGMA optimize;
        """
    )
    connection.commit()
    after = connection.execute("PRAGMA page_count").fetchone()[0] * connection.execute("PRAGMA page_size").fetchone()[0]
    elapsed = time.perf_counter() - started
    print_progress(f"Indexes complete in {elapsed:.1f}s; added {mib(after - before):,.1f} MiB")
    return {
        "seconds": round(elapsed, 3),
        "bytes_before": before,
        "bytes_after": after,
        "index_bytes_added": after - before,
    }


def table_sizes(connection: sqlite3.Connection) -> dict[str, dict[str, Any]]:
    try:
        rows = connection.execute(
            """
            SELECT name, SUM(pgsize), COUNT(*)
            FROM dbstat
            GROUP BY name
            ORDER BY SUM(pgsize) DESC
            """
        ).fetchall()
    except sqlite3.OperationalError:
        return {}
    return {name: {"bytes": int(size), "mib": mib(int(size)), "pages": int(pages)} for name, size, pages in rows}


def verify_texts(connection: sqlite3.Connection) -> dict[str, Any]:
    started = time.perf_counter()
    failures = []
    verified = 0
    raw_bytes = 0
    compressed_bytes = 0
    cursor = connection.execute(
        "SELECT id, uncompressed_bytes, compressed_bytes, text_sha256, content_zlib FROM versions ORDER BY id"
    )
    for version_id, expected_raw, expected_compressed, expected_hash, compressed in cursor:
        try:
            raw = zlib.decompress(compressed)
            if len(raw) != expected_raw or len(compressed) != expected_compressed:
                failures.append({"id": version_id, "reason": "size"})
            elif hashlib.sha256(raw).digest() != expected_hash:
                failures.append({"id": version_id, "reason": "hash"})
            elif not isinstance(json.loads(raw), (list, dict)):
                failures.append({"id": version_id, "reason": "shape"})
        except Exception as exc:  # verification must report corrupt rows
            failures.append({"id": version_id, "reason": type(exc).__name__})
        verified += 1
        raw_bytes += expected_raw
        compressed_bytes += expected_compressed
        if verified % 2000 == 0:
            print_progress(f"Verified {verified:,} compressed text versions")
    elapsed = time.perf_counter() - started
    return {
        "seconds": round(elapsed, 3),
        "verified": verified,
        "failures": failures,
        "uncompressed_bytes": raw_bytes,
        "compressed_bytes": compressed_bytes,
    }


LINK_QUERY = """
WITH anchor AS (SELECT id FROM refs WHERE reference = ?)
SELECT l.id,
       CASE e.side WHEN 0 THEN target1.reference ELSE target0.reference END AS target_ref,
       l.link_type,
       CASE e.side WHEN 0 THEN l.work1_id ELSE l.work0_id END AS target_work_id,
       CASE e.side WHEN 0 THEN l.available1 ELSE l.available0 END AS target_languages
FROM anchor
JOIN link_endpoints e ON e.ref_id = anchor.id
JOIN links l ON l.id = e.link_id
JOIN refs target0 ON target0.id = l.ref0_id
JOIN refs target1 ON target1.id = l.ref1_id
ORDER BY l.id
LIMIT ?
"""


def verify_queries(
    connection: sqlite3.Connection,
    manifest_path: Path,
) -> dict[str, Any]:
    _, references = load_manifest(manifest_path)
    results = {}
    timings = []
    for reference in references:
        count_started = time.perf_counter()
        count = connection.execute(
            """
            SELECT COUNT(*)
            FROM refs r
            JOIN link_endpoints e ON e.ref_id = r.id
            WHERE r.reference = ?
            """,
            (reference,),
        ).fetchone()[0]
        count_ms = (time.perf_counter() - count_started) * 1000
        page_started = time.perf_counter()
        rows = connection.execute(LINK_QUERY, (reference, 100)).fetchall()
        page_ms = (time.perf_counter() - page_started) * 1000
        timings.extend((count_ms, page_ms))
        results[reference] = {
            "count": count,
            "returned": len(rows),
            "count_ms": round(count_ms, 3),
            "first_page_ms": round(page_ms, 3),
        }
    sorted_timings = sorted(timings)
    return {
        "references": results,
        "minimum_ms": round(min(timings, default=0.0), 3),
        "median_ms": round(sorted_timings[len(sorted_timings) // 2], 3) if sorted_timings else 0.0,
        "maximum_ms": round(max(timings, default=0.0), 3),
    }


def verify_reader_queries(
    connection: sqlite3.Connection,
    manifest_path: Path,
) -> dict[str, Any]:
    works, _ = load_manifest(manifest_path)
    measurements = []
    failures = []
    for work in works:
        title = work["title"]
        for language in ("he", "en"):
            started = time.perf_counter()
            row = connection.execute(
                """
                SELECT v.id, v.version_title, v.segment_count, v.uncompressed_bytes, v.compressed_bytes,
                       v.text_sha256, v.content_zlib
                FROM versions v
                JOIN works w ON w.id = v.work_id
                WHERE w.title = ? AND v.actual_language = ? AND v.segment_count > 0
                ORDER BY v.is_primary DESC, v.priority DESC, v.character_count DESC
                LIMIT 1
                """,
                (title, language),
            ).fetchone()
            if row is None:
                failures.append({"title": title, "language": language, "reason": "missing"})
                continue
            version_id, version_title, segment_count, raw_size, compressed_size, expected_hash, compressed = row
            fetched = time.perf_counter()
            raw = zlib.decompress(compressed)
            decompressed = time.perf_counter()
            content = json.loads(raw)
            parsed = time.perf_counter()
            if hashlib.sha256(raw).digest() != expected_hash:
                failures.append({"title": title, "language": language, "reason": "hash"})
            measurements.append(
                {
                    "title": title,
                    "language": language,
                    "version_id": version_id,
                    "version_title": version_title,
                    "shape": type(content).__name__,
                    "segments": segment_count,
                    "uncompressed_bytes": raw_size,
                    "compressed_bytes": compressed_size,
                    "fetch_ms": round((fetched - started) * 1000, 3),
                    "decompress_ms": round((decompressed - fetched) * 1000, 3),
                    "parse_ms": round((parsed - decompressed) * 1000, 3),
                    "total_ms": round((parsed - started) * 1000, 3),
                }
            )
    timings = sorted(item["total_ms"] for item in measurements)
    return {
        "requested": len(works) * 2,
        "opened": len(measurements),
        "failures": failures,
        "minimum_ms": round(min(timings, default=0.0), 3),
        "median_ms": round(timings[len(timings) // 2], 3) if timings else 0.0,
        "maximum_ms": round(max(timings, default=0.0), 3),
        "measurements": measurements,
    }


def verify_database(
    connection: sqlite3.Connection,
    manifest_path: Path,
) -> dict[str, Any]:
    print_progress("Running SQLite integrity check")
    integrity_started = time.perf_counter()
    integrity = connection.execute("PRAGMA integrity_check").fetchone()[0]
    integrity_seconds = time.perf_counter() - integrity_started
    print_progress(f"Integrity check: {integrity} in {integrity_seconds:.1f}s")
    text_result = verify_texts(connection)
    print_progress(
        f"Text verification: {text_result['verified']:,} versions, "
        f"{len(text_result['failures']):,} failures in {text_result['seconds']:.1f}s"
    )
    query_result = verify_queries(connection, manifest_path)
    reader_result = verify_reader_queries(connection, manifest_path)
    print_progress(
        f"Reader checks: {reader_result['opened']:,}/{reader_result['requested']:,} versions opened; "
        f"median {reader_result['median_ms']:.1f}ms, maximum {reader_result['maximum_ms']:.1f}ms"
    )
    counts = {
        table: int(connection.execute(f"SELECT COUNT(*) FROM {table}").fetchone()[0])
        for table in ("works", "terms", "categories", "people", "versions", "refs", "links", "link_endpoints")
    }
    return {
        "passed": integrity == "ok" and not text_result["failures"] and not reader_result["failures"],
        "integrity": integrity,
        "integrity_seconds": round(integrity_seconds, 3),
        "counts": counts,
        "texts": text_result,
        "link_queries": query_result,
        "reader_queries": reader_result,
    }


def build(args: argparse.Namespace) -> int:
    started = time.perf_counter()
    dump_dir = args.dump_dir.resolve()
    database = args.database.resolve()
    report_path = args.report.resolve()
    connection = create_database(database, args.force)
    report: dict[str, Any] = {
        "schema_version": SCHEMA_VERSION,
        "dump_directory": str(dump_dir),
        "database": str(database),
        "started_utc": utc_now(),
    }
    try:
        work_ids, works, _, _ = import_small_metadata(connection, dump_dir)
        report["metadata_database_bytes"] = allocated_database_bytes(connection)
        report["texts_import"] = import_all_versions(
            connection,
            require_collection(dump_dir, "texts"),
            work_ids,
            works,
        )
        report["links_import"] = import_all_links(connection, require_collection(dump_dir, "links"), works)
        report["indexes"] = create_final_indexes(connection)
        connection.executemany(
            "INSERT OR REPLACE INTO metadata(key, value) VALUES (?, ?)",
            [
                ("dump_directory", str(dump_dir)),
                ("text_compression", f"zlib-{TEXT_COMPRESSION_LEVEL}"),
                ("source_texts_bson_bytes", str(require_collection(dump_dir, "texts").stat().st_size)),
                ("source_links_bson_bytes", str(require_collection(dump_dir, "links").stat().st_size)),
            ],
        )
        connection.commit()
        report["verification"] = verify_database(connection, args.manifest)
        report["table_sizes"] = table_sizes(connection)
        report["portable_storage_breakdown"] = {
            "metadata_and_schema_bytes": report["texts_import"]["database_bytes_before"],
            "text_phase_bytes_added": report["texts_import"]["database_bytes_added"],
            "link_phase_bytes_added": report["links_import"]["database_bytes_added"],
            "index_phase_bytes_added": report["indexes"]["index_bytes_added"],
        }
        report["database_bytes"] = database.stat().st_size
        report["database_gib"] = gib(database.stat().st_size)
        report["total_seconds"] = round(time.perf_counter() - started, 3)
        report["completed_utc"] = utc_now()
        report_path.parent.mkdir(parents=True, exist_ok=True)
        report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        summary = {
            "passed": report["verification"]["passed"],
            "database_gib": report["database_gib"],
            "total_seconds": report["total_seconds"],
            "counts": report["verification"]["counts"],
            "text_compression_ratio": report["texts_import"]["compression_ratio"],
            "link_query_ms": {
                key: report["verification"]["link_queries"][key]
                for key in ("minimum_ms", "median_ms", "maximum_ms")
            },
            "storage_breakdown_gib": {
                key: gib(value) for key, value in report["portable_storage_breakdown"].items()
            },
        }
        print(json.dumps(summary, indent=2), flush=True)
        print_progress(f"Report: {report_path}")
        print_progress(f"Database: {database}")
        return 0 if report["verification"]["passed"] else 1
    finally:
        connection.close()


def verify(args: argparse.Namespace) -> int:
    connection = sqlite3.connect(args.database)
    try:
        result = verify_database(connection, args.manifest)
        if args.report is not None:
            args.report.parent.mkdir(parents=True, exist_ok=True)
            args.report.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 0 if result["passed"] else 1
    finally:
        connection.close()


def parser() -> argparse.ArgumentParser:
    scripts_dir = Path(__file__).resolve().parent
    default_manifest = scripts_dir / "sefaria_dump_samples.json"
    result = argparse.ArgumentParser(description=__doc__)
    commands = result.add_subparsers(dest="command", required=True)

    build_parser = commands.add_parser("build", help="Build and verify the complete optimized database")
    build_parser.add_argument("--dump-dir", type=Path, required=True)
    build_parser.add_argument("--database", type=Path, default=Path("artifacts/sefaria-full.sqlite"))
    build_parser.add_argument("--report", type=Path, default=Path("artifacts/sefaria-full-report.json"))
    build_parser.add_argument("--manifest", type=Path, default=default_manifest)
    build_parser.add_argument("--force", action="store_true")
    build_parser.set_defaults(handler=build)

    verify_parser = commands.add_parser("verify", help="Verify a previously built complete database")
    verify_parser.add_argument("--database", type=Path, default=Path("artifacts/sefaria-full.sqlite"))
    verify_parser.add_argument("--manifest", type=Path, default=default_manifest)
    verify_parser.add_argument("--report", type=Path, default=Path("artifacts/sefaria-full-verification.json"))
    verify_parser.set_defaults(handler=verify)
    return result


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    args = parser().parse_args()
    return int(args.handler(args))


if __name__ == "__main__":
    sys.exit(main())
