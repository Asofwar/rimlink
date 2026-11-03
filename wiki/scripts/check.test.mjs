import test from 'node:test';
import assert from 'node:assert/strict';
import { validatePages } from './check-lib.mjs';

const manifest = { pages: [{id:'index', contentRevision:1, sections:['usage']}] };
const page = `---
pageId: index
contentRevision: 1
---
# Wiki

## Use {#usage}

Real instructions for a reader.
`;
test('accepts matching locale articles and a real target anchor', () => {
  assert.deepEqual(validatePages(manifest, {'ru/index.md':page+'[Help](index.md#usage)', 'en/index.md':page}), []);
});
test('rejects metadata prefix spoofing', () => {
 assert.match(validatePages(manifest, {'ru/index.md':page.replace('contentRevision: 1','contentRevision: 10'), 'en/index.md':page}).join(' '), /revision mismatch/);
});
test('rejects a missing translation', () => {
  assert.match(validatePages(manifest, {'ru/index.md':page}).join(' '), /en.index.md/);
});
test('rejects missing anchors even when target exists', () => {
  assert.match(validatePages(manifest, {'ru/index.md':page+'[Help](index.md#lost)', 'en/index.md':page}).join(' '), /lost/);
});
test('rejects translation section drift', () => {
  assert.match(validatePages(manifest, {'ru/index.md':page, 'en/index.md':`# Wiki
Some text.`}).join(' '), /usage/);
});
test('rejects missing and cross-language page links', () => {
  const result = validatePages(manifest, {'ru/index.md':page+'[Bad](missing.md) [Wrong](../en/index.md)', 'en/index.md':page});
  assert.match(result.join(' '), /missing.md/); assert.match(result.join(' '), /cross-locale/);
});
