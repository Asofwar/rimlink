import {readFileSync,readdirSync} from 'node:fs';
import {resolve,relative} from 'node:path';
import {optionSurface,routeSurface,adminCommands,digest} from './reference-lib.mjs';
const root=resolve(import.meta.dirname,'../..'), wiki=resolve(root,'wiki');
const inv=JSON.parse(readFileSync(resolve(wiki,'reference-inventory.json'))), errors=[];
const read=p=>readFileSync(resolve(root,p),'utf8').replaceAll(String.fromCharCode(13),'').trimEnd();
for(const [file,hash] of Object.entries(inv.sources)) if(digest(read(file))!==hash) errors.push('source changed: '+file+' — review both translations and refresh the inventory');
function walk(dir){return readdirSync(dir,{withFileTypes:true}).flatMap(e=>['bin','obj','node_modules','.git'].includes(e.name)?[]:e.isDirectory()?walk(resolve(dir,e.name)):[resolve(dir,e.name)]);}
const all=walk(resolve(root,'src/Rimlink.Server')).filter(p=>p.endsWith('.cs'));
const options=all.flatMap(p=>optionSurface(readFileSync(p,'utf8')));
const routes=all.flatMap(p=>routeSurface(readFileSync(p,'utf8')));
for(const m of read('src/Rimlink.Server/Program.cs').matchAll(/app[.]Map(HealthChecks|Metrics)[(]"([^"]+)"[)]/g))routes.push({method:'GET',path:m[2],scope:'public'});
for(const p of all){const text=readFileSync(p,'utf8');if((optionSurface(text).length||routeSurface(text).length)&&!inv.sources[relative(root,p).replaceAll(String.fromCharCode(92),'/')])errors.push('new reference source missing from snapshot: '+relative(root,p));}
const shape=(rows,keys)=>rows.map(r=>keys.map(k=>r[k]).join('|')).sort();
if(JSON.stringify(shape(options,['key','type','default']))!==JSON.stringify(shape(inv.options,['key','type','default'])))errors.push('option inventory exact parity mismatch (added/removed/changed)');
const normalized=routes.map(r=>['/health','/metrics'].includes(r.path)?{...r,scope:'public'}:r);
if(JSON.stringify(shape(normalized,['method','path','scope']))!==JSON.stringify(shape(inv.routes,['method','path','scope'])))errors.push('route inventory exact parity mismatch (added/removed/changed)');
for(const o of options) if(!inv.options.some(x=>x.key===o.key&&x.type===o.type&&x.default===o.default)) errors.push('undocumented option '+o.key);
for(const r of routes) if(!inv.routes.some(x=>x.method===r.method&&x.path===r.path&&x.scope===r.scope)) errors.push('undocumented route '+r.method+' '+r.path);
const commands=adminCommands(read('src/Rimlink.Tools/Commands/AdminCommand.cs'));
if(JSON.stringify(commands)!==JSON.stringify(inv.adminCommands)) errors.push('CLI inventory drift');
for(const lang of ['ru','en']) {
 const config=readFileSync(resolve(wiki,lang,'admins/configuration.md'),'utf8'), api=readFileSync(resolve(wiki,lang,'admins/api.md'),'utf8'),cli=readFileSync(resolve(wiki,lang,'admins/cli.md'),'utf8');
 for(const o of inv.options) if(!config.includes(o.key)) errors.push(lang+' missing key '+o.key);
 for(const r of inv.routes) if(!api.includes('| '+r.method+' | '+String.fromCharCode(96)+r.path+String.fromCharCode(96))) errors.push(lang+' missing route '+r.path);
 for(const c of commands) if(!cli.includes(c)) errors.push(lang+' missing CLI '+c);
}
const manifest=JSON.parse(readFileSync(resolve(wiki,'content-manifest.json')));
if(!read('mod/About/About.xml').includes('<modVersion>'+manifest.productVersion+'</modVersion>'))errors.push('mod version drift');
if(!read('src/Rimlink.Client.Core/ClientInfo.cs').includes('Version = '+String.fromCharCode(34)+manifest.productVersion+String.fromCharCode(34)))errors.push('client version drift');
if(!read('Directory.Build.props').includes('<Version>'+manifest.productVersion+'</Version>')) errors.push('product version drift');
if(!read('src/Rimlink.Protocol/ProtocolVersion.cs').includes('Current = '+manifest.protocolVersion+';')) errors.push('protocol version drift');
console.log(inv.options.length+' keys, '+inv.routes.length+' HTTP operations, '+commands.length+' admin commands');
errors.forEach(e=>console.error(e));if(errors.length) process.exitCode=1;
