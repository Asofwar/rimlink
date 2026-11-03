// Deliberately bounded to the repository's current C# declaration style.
// Unknown declaration styles must fail coverage checks rather than disappear silently.
import {createHash} from 'node:crypto';
export const digest = text => createHash('sha256').update(text).digest('hex');
export function optionSurface(text) {
 const rows=[];
 if(/public (?:(?:abstract|partial|static) )?(?:class|record) [A-Za-z0-9_]*Options/.test(text))throw new Error('Unknown options declaration: inventory explicitly before accepting it');
 for(const block of text.split('public sealed class ').slice(1)) {
  const name=block.split(/[ (<{]/)[0].trim();
  const section=block.match(/const string Section = "([^"]+)"/)?.[1] ?? ({ApiTokenOptions:'Admin:Tokens:*',IncidentCatalogEntry:'Incidents:Catalog:*'}[name]);
  if(!section){if(name.endsWith('Options'))throw new Error('Options without inventoried binding: '+name);continue;}
  for(const m of block.matchAll(/public ([^ ]+) ([A-Za-z0-9_]+) [{] get; set; [}](?: = ([^;]+);)?/g)) {
   rows.push({key:section+':'+m[2],type:m[1],default:m[3]??(m[1].includes('?')?'null':m[1]==='bool'?'false':'0')});
  }
 }
 return rows;
}
export function routeSurface(text) {
 const allMaps=[...text.matchAll(/[.]Map(Get|Post|Put|Delete|Patch|Methods|Head|Options)[(]/g)];
 if(allMaps.some(m=>!['Get','Post','Put','Delete'].includes(m[1])))throw new Error('Unknown HTTP mapping; extend the audited extractor');
 const groups=new Map([...text.matchAll(/var ([a-z]+) = app[.]MapGroup[(]"([^"]+)"[)]/g)].map(m=>[m[1],m[2]]));
 const matches=[...text.matchAll(/(?:app|group|admin|bootstrap|[.]RequireAdmin[(][)])[.]Map(Get|Post|Put|Delete)[(]"([^"]*)"/g)];
 if(matches.length!==allMaps.length)throw new Error('Unknown HTTP receiver or declaration; extend the audited extractor');
 return matches.map((m,i)=>{
  const receiver=m[0].split('.')[0];
  const prefix=groups.get(receiver)??(m[0].startsWith('.RequireAdmin')?'/api/admin':'');
  const chunk=text.slice(m.index,matches[i+1]?.index??text.length);
  return {method:m[1].toUpperCase(),path:(prefix+m[2]).replace(/[/]$/,''),scope:chunk.match(/RequireScope[(]ApiScope[.]([A-Za-z]+)[)]/)?.[1]?.toLowerCase()??'special'};
 });
}
export function adminCommands(text) {
 const main=text.slice(text.indexOf('return command switch'),text.indexOf('private static async Task<string> RunInfluence'));
 const arms=[...main.matchAll(/^( +)"([a-z-]+)"(?: when .*?)? =>/gm)];
 const depth=Math.min(...arms.map(m=>m[1].length));
 const direct=arms.filter(m=>m[1].length===depth).map(m=>m[2]);
 const influence=[...text.slice(text.indexOf('int expected =')).matchAll(/"(influence[a-z-]*)"/g)].map(m=>m[1]);
 return [...new Set([...direct,...influence])].sort();
}
