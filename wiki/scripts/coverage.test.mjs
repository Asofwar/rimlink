import test from 'node:test';
import assert from 'node:assert/strict';
import {validatePages} from './check-lib.mjs';
const page={id:'guide',kind:'guide',coverage:[{id:'recover',questions:{ru:'Как восстановить передачу?',en:'How do I recover a transfer?'},sources:['src/Transfer.cs']}]};
const docs=(body='Реши исходный запрос после входа, сохрани журнал операции.')=>({'ru/guide.md':`## Восстановление {#recover}

`+body,'en/guide.md':`## Recovery {#recover}

`+body});
test('coverage rejects a missing scenario anchor',()=>assert(validatePages({pages:[page]}, {'ru/guide.md':'Old short article','en/guide.md':'Old short article'}).some(e=>e.includes('coverage'))));
test('coverage rejects scenarios without source evidence',()=>assert(validatePages({pages:[{...page,coverage:[{...page.coverage[0],sources:[]}]}]},docs()).some(e=>e.includes('source'))));
test('coverage accepts localized questions and authored scenario bodies',()=>assert.deepEqual(validatePages({pages:[page]},docs()),[]));
test('coverage rejects empty scenarios and missing language questions',()=>assert(validatePages({pages:[{...page,coverage:[{...page.coverage[0],questions:{ru:'Вопрос'}}]}]},docs('')).length>=2));
