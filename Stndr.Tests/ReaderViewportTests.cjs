const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');

// Exercise the actual embedded browser function without starting a native WebView.
const source = fs.readFileSync(path.join(__dirname, '../Stndr/MainWindow.ReaderWebView.cs'), 'utf8');
const start = source.indexOf('const chapterCandidates =');
const end = source.indexOf('let scrollTimer =', start);
assert.ok(start >= 0 && end > start);
const script = source.slice(start, end) + '\ngetVisibleChapter();';

function visibleChapter(boxes, offset = 0) {
    let measurements = 0;
    const nodes = boxes.map(([top, bottom], index) => ({
        dataset: { chapter: String(index) },
        getBoundingClientRect() {
            measurements++;
            return { top: top - offset, bottom: bottom - offset };
        }
    }));
    const chapter = vm.runInNewContext(script, {
        document: { querySelectorAll: () => nodes },
        window: { innerHeight: 800 }
    });
    return { chapter, measurements };
}

test('chapter lookup handles empty documents, viewport gaps and tall paragraphs', () => {
    assert.equal(visibleChapter([]).chapter, '');
    assert.equal(visibleChapter([[900, 1000]]).chapter, '');
    assert.equal(visibleChapter([[-200, -10]]).chapter, '');
    assert.equal(visibleChapter([[-200, 1600], [1620, 1800]]).chapter, '0');
    assert.equal(visibleChapter([[-200, 200], [10, 50], [70, 100]]).chapter, '1');
    assert.equal(visibleChapter([[-100, 0], [16, 100]]).chapter, '0');
});

test('50,000 paragraphs require fewer than 40 measurements at any tested scroll position', () => {
    const boxes = Array.from({ length: 50000 }, (_, i) => [i * 136, i * 136 + 120]);
    for (const index of [0, 1, 1000, 25000, 49999]) {
        const result = visibleChapter(boxes, index * 136 + 20);
        assert.equal(result.chapter, String(index));
        assert.ok(result.measurements < 40, `Measured ${result.measurements} paragraphs`);
    }
});
