import {readFileSync,existsSync,statSync,readdirSync} from 'node:fs';
import {validatePages,validateCatalog} from './check-lib.mjs';
const manifest=JSON.parse(readFileSync(new URL('../content-manifest.json',import.meta.url)));
const files={};
for(const p of manifest.pages) for(const locale of ['ru','en']) {
 const name=locale+'/'+p.id+'.md';
 try {files[name]=readFileSync(new URL('../'+name,import.meta.url),'utf8');} catch(e) {if(e.code!=='ENOENT') throw e;}
}
const baseline=JSON.parse(readFileSync(new URL('../legacy-anchors.json',import.meta.url)));
for(const locale of ['ru','en'])for(const name of readdirSync(new URL('../'+locale+'/',import.meta.url),{recursive:true}))if(name.endsWith('.md')){const normalized=locale+'/'+name.split(String.fromCharCode(92)).join('/');if(!files[normalized])files[normalized]=readFileSync(new URL('../'+normalized,import.meta.url),'utf8');}
const errors=[...validatePages(manifest,files),...validateCatalog(manifest,baseline,files)];
for(const p of manifest.pages)for(const t of p.coverage??[])for(const s of t.sources??[]){const url=new URL('../../'+s,import.meta.url);if(!existsSync(url)||!statSync(url).isFile())errors.push(p.id+' invalid source '+s);}
for(const error of errors) console.error(error);
console.log(manifest.pages.length+' pairs; '+errors.length+' errors');
if(errors.length) process.exitCode=1;
