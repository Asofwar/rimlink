import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync,readdirSync} from 'node:fs';
import {fileURLToPath} from 'node:url';
const root=fileURLToPath(new URL('../',import.meta.url));
function articles(dir){return readdirSync(dir,{withFileTypes:true}).flatMap(e=>e.isDirectory()?articles(dir+'/'+e.name):e.name.endsWith('.md')?[dir+'/'+e.name]:[]);}
test('authored locale articles contain no undefined placeholder',()=>{for(const p of ['ru','en'].flatMap(l=>articles(root+l)))assert.ok(!readFileSync(p,'utf8').includes('undefined'),p);});
test('configuration states real-time versus colony game-day exception',()=>{for(const l of ['ru','en']){const s=readFileSync(root+l+'/admins/configuration.md','utf8');assert.ok(s.includes('Hygiene:ArchiveMaxColonyDays'),l);assert.ok(s.includes(l==='ru'?'игровые дни':'game days'),l);assert.ok(!s.includes('Hours/Days/Minutes/Seconds suffixes use real time'),l);}});
test('REST reference defines response shapes and empty-body auth handling',()=>{for(const l of ['ru','en']){const s=readFileSync(root+l+'/admins/api.md','utf8');assert.ok(s.includes('{#response-schemas}'),l);assert.ok(s.includes('response.ok'),l);assert.ok(s.includes('economyFreezeReason: string?'),l);}});
