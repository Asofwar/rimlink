---
pageId: developers/localization
contentRevision: 1
---

# Localization

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

Every mod-facing string is a keyed string under mod/Languages/English and Russian. Add both versions in the same task with matching keys/placeholders; do not translate technical identifiers. Inspect actual dialogs, label lengths and grammar in context.

The wiki uses matching pageIds and shared explicit anchors in RU/EN. Both articles carry the same contentRevision. A semantic change requires updating both versions and the manifest, not replacing English with a link to Russian. Navigation labels are translated too. Search runs locally and normalizes case and ё/е.

Run test:checks and check:content after translating; manually compare meaning, warnings and numeric limits. Matching metadata does not prove translation quality.

## Limits and safety {#limits}

Do not hard-code C# UI text instead of localization keys. Do not add external search/analytics for localization. Examples must not contain real tokens, personal saves or private logs.

## See also {#related}

- [Contributing and wiki maintenance](./contributing.md)
- [Testing and acceptance](./testing.md)
- [Glossary](../glossary.md)
