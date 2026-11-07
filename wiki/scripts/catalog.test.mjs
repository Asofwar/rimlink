import test from 'node:test';
import assert from 'node:assert/strict';
import {validateCatalog} from './check-lib.mjs';
const page={id:'a',kind:'guide',group:'g',titles:{ru:'А',en:'A'},coverage:[{id:'x'}]};
const manifest={pages:[page],groups:[{id:'g',titles:{ru:'Г',en:'G'}}]};
test('catalog rejects missing kind, localization, group and coverage',()=>{for(const edit of [{kind:null},{titles:{ru:'А'}},{group:'missing'},{coverage:[]}])assert(validateCatalog({...manifest,pages:[{...page,...edit}]},{pages:[]}).length>0);});
test('baseline rejects removed page and old anchor',()=>{const old={pages:[{id:'a',anchors:{ru:['old'],en:['old']}},{id:'removed',anchors:{}}]};const errors=validateCatalog(manifest,old,{'ru/a.md':'','en/a.md':''});assert(errors.some(x=>x.includes('removed')));assert(errors.some(x=>x.includes('old')));});
test('catalog accepts complete metadata and preserved anchors',()=>assert.deepEqual(validateCatalog(manifest,{pages:[{id:'a',anchors:{ru:['old'],en:['old']}}]},{'ru/a.md':'{#old}','en/a.md':'{#old}'}),[]));
