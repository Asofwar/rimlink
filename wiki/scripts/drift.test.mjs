import test from 'node:test';
import assert from 'node:assert/strict';
import {validateDrift} from './drift-lib.mjs';
const m={pages:[{id:'players/live',sourcePaths:['src/live/']}]};
test('changed source requires both locales',()=>assert.ok(validateDrift(m,['src/live/Relay.cs','wiki/ru/players/live.md']).length));
test('both locale updates satisfy source drift',()=>assert.deepEqual(validateDrift(m,['src/live/Relay.cs','wiki/ru/players/live.md','wiki/en/players/live.md']),[]));
test('unrelated files have no doc impact',()=>assert.deepEqual(validateDrift(m,['src/trade.cs']),[]));
test('rationale must cover the exact changed source set',()=>{const e={pageId:'players/live',sources:['src/live/Relay.cs'],reason:'Internal comment correction only; user-visible behavior is unchanged.'};e.base='base';e.reviewer='maintainer';e.after={'src/live/Relay.cs':'hash'};assert.deepEqual(validateDrift(m,['src/live/Relay.cs'],[e],{base:'base',exceptionChanged:true,digests:e.after}),[]);assert.equal(validateDrift(m,['src/live/Relay.cs','src/live/Other.cs'],[e]).length,1);});
