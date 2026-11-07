import test from 'node:test';
import assert from 'node:assert/strict';
import {validateRenderedAnchors} from './check-lib.mjs';
test('rendered baseline includes automatic heading ids, not only explicit markdown anchors',()=>{
 const baseline={pages:[{id:'index',renderedAnchors:{en:['welcome'],ru:['добро-пожаловать']}}]};
 assert.equal(validateRenderedAnchors(baseline,{'en/index.html':'<h1 id="new">New</h1>','ru/index.html':'<span id="добро-пожаловать"></span>'}).length,1);
 assert.deepEqual(validateRenderedAnchors(baseline,{'en/index.html':'<span id="welcome"></span>','ru/index.html':'<span id="добро-пожаловать"></span>'}),[]);
});
