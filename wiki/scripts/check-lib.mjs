import path from 'node:path';
export function validatePages(manifest, files) {
 const errors=[], ids=new Set();
 for(const page of manifest.pages) {
  if(ids.has(page.id)) errors.push('duplicate '+page.id);
  ids.add(page.id);
  for(const locale of ['ru','en']) {
   const name=locale+'/'+page.id+'.md', text=files[name];
   if(!text) {errors.push('missing '+name);continue;}
   for(const section of page.sections??[]) if(!text.includes('{#'+section+'}')) errors.push(name+' missing section '+section);
   for(const section of page.sections??[]) {
    const tail=text.slice(text.indexOf('{#'+section+'}')+section.length+3).split(String.fromCharCode(10)).slice(1);
    const body=[];for(const line of tail){if(line.startsWith('## '))break;body.push(line);}
    if(body.join('').trim().length<12)errors.push(name+' empty section '+section);
   }
   const lines=text.split(String.fromCharCode(10)).map(line=>line.trim()), end=lines.indexOf('---',1), metadata=lines[0]==='---'&&end>0?lines.slice(1,end):[];
   if(page.contentRevision && !metadata.includes('contentRevision: '+page.contentRevision)) errors.push(name+' revision mismatch');
   if(page.contentRevision && !metadata.includes('pageId: '+page.id)) errors.push(name+' pageId mismatch');
   for(const match of text.matchAll(/!?\[[^\]]*\]\(<?([^ )>]+)>?\)/g)) {
    const href=match[1];
    if(/^(https?:|mailto:)/.test(href)) continue;
    const [target,anchor]=href.split('#');
    if(target.startsWith('/')) {errors.push(name+' absolute link '+href);continue;}
    let resolved=target?path.posix.normalize(path.posix.join(path.posix.dirname(name),target)):name;
    if(resolved.endsWith('/')) resolved+='index.md';
    if(!path.posix.extname(resolved)) resolved+='.md';
    if(resolved.split('/')[0]!==locale) errors.push(name+' cross-locale '+href);
    if(!files[resolved]) errors.push(name+' missing link '+href);
    else if(anchor && !files[resolved].includes('{#'+anchor+'}')) errors.push(name+' missing anchor '+href);
   }
  }
 }
 return errors;
}
