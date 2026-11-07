import {readFileSync} from 'node:fs';
import {execFileSync} from 'node:child_process';
import {resolve} from 'node:path';
import {validateDrift} from './drift-lib.mjs';
import {digest} from './reference-lib.mjs';
const root=resolve(import.meta.dirname,'../..'),args=process.argv.slice(2),i=args.indexOf('--base');
if(i<0||!args[i+1]||args[i+1].startsWith('-'))throw new Error('Pass an explicit --base <commit>; no implicit HEAD default');
const base=execFileSync('git',['rev-parse','--verify',args[i+1]+'^{commit}'],{cwd:root,encoding:'utf8'}).trim();
const changed=execFileSync('git',['diff','--name-only',base,'--'],{cwd:root,encoding:'utf8'}).trim().split(String.fromCharCode(10)).filter(Boolean);
changed.push(...execFileSync('git',['ls-files','--others','--exclude-standard'],{cwd:root,encoding:'utf8'}).trim().split(String.fromCharCode(10)).filter(Boolean));
const manifest=JSON.parse(readFileSync(resolve(root,'wiki/content-manifest.json'))), exceptions=JSON.parse(readFileSync(resolve(root,'wiki/docs-impact.json')));
const digests=Object.fromEntries(changed.map(f=>{try{return [f,digest(readFileSync(resolve(root,f)))];}catch(e){if(e.code!=='ENOENT')throw e;return [f,'deleted'];}}));
const errors=validateDrift(manifest,changed,exceptions,{base,digests,exceptionChanged:changed.includes('wiki/docs-impact.json')});
let previous;
try { previous=JSON.parse(execFileSync('git',['show',base+':wiki/content-manifest.json'],{cwd:root,encoding:'utf8',stdio:['ignore','pipe','ignore']})); } catch { /* First wiki baseline has no previous manifest. */ }
if(previous)for(const page of manifest.pages){const before=previous.pages.find(p=>p.id===page.id);if(before&&['ru','en'].every(l=>changed.includes('wiki/'+l+'/'+page.id+'.md'))&&page.contentRevision<=before.contentRevision)errors.push(page.id+': increase contentRevision in manifest and both articles');}
errors.forEach(e=>console.error(e));console.log('Explicit base '+base+'; '+changed.length+' changed paths; '+errors.length+' drift errors');if(errors.length)process.exitCode=1;
