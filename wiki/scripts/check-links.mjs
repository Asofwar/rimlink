import {readFileSync,readdirSync,existsSync} from 'node:fs';
import {resolve,relative,dirname} from 'node:path';
import {validateRenderedAnchors} from './check-lib.mjs';
const root=resolve(import.meta.dirname,'../.vitepress/dist'), errors=[], htmlFiles={};
function walk(dir){return readdirSync(dir,{withFileTypes:true}).flatMap(e=>e.isDirectory()?walk(resolve(dir,e.name)):[resolve(dir,e.name)]);}
let count=0;
for(const file of walk(root).filter(f=>f.endsWith('.html'))) {
 const text=readFileSync(file,'utf8');count++;htmlFiles[relative(root,file).split(String.fromCharCode(92)).join('/')]=text;
 for(const m of text.matchAll(/(?:href|src)="([^"]+)"/g)) {
  const href=m[1].replaceAll('&amp;','&');
  if(/^(https?:|data:|mailto:|javascript:)/.test(href)) continue;
  const [url,hash]=href.split('#');let target;
  if(url.startsWith('/')&&!url.startsWith('/rimlink/')){errors.push(relative(root,file)+' wrong base '+href);continue;}
  target=url?resolve(url.startsWith('/')?root:dirname(file),url.startsWith('/')?url.slice('/rimlink/'.length):url):file;
  target=target.split('?')[0];if(existsSync(target+'.html'))target+='.html';else if(existsSync(resolve(target,'index.html')))target=resolve(target,'index.html');
  if(!existsSync(target)){errors.push(relative(root,file)+' missing '+href);continue;}
  if(hash&&target.endsWith('.html')&&!readFileSync(target,'utf8').includes('id="'+decodeURIComponent(hash)+'"'))errors.push(relative(root,file)+' missing anchor '+href);
 }
}
errors.push(...validateRenderedAnchors(JSON.parse(readFileSync(new URL('../legacy-anchors.json',import.meta.url))),htmlFiles));
console.log(count+' HTML pages; '+errors.length+' broken links');errors.forEach(e=>console.error(e));if(errors.length)process.exitCode=1;
