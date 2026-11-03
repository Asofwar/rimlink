import {readFileSync} from 'node:fs';
import {validatePages} from './check-lib.mjs';
const manifest=JSON.parse(readFileSync(new URL('../content-manifest.json',import.meta.url)));
const files={};
for(const p of manifest.pages) for(const locale of ['ru','en']) {
 const name=locale+'/'+p.id+'.md';
 try {files[name]=readFileSync(new URL('../'+name,import.meta.url),'utf8');} catch(e) {if(e.code!=='ENOENT') throw e;}
}
const errors=validatePages(manifest,files);
for(const error of errors) console.error(error);
console.log(manifest.pages.length+' pairs; '+errors.length+' errors');
if(errors.length) process.exitCode=1;
