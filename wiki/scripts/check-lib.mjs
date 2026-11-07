import path from 'node:path';
export function validateRenderedAnchors(baseline, htmlFiles) {
 const errors=[];
 for(const page of baseline.pages)for(const locale of ['ru','en'])for(const anchor of page.renderedAnchors?.[locale]??[])if(!htmlFiles[locale+'/'+page.id+'.html']?.includes('id="'+anchor+'"'))errors.push('removed rendered anchor '+locale+'/'+page.id+'#'+anchor);
 return errors;
}
export function validateCatalog(manifest, baseline={pages:[]}, files={}) {
 const errors=[], ids=new Set(manifest.pages.map(p=>p.id));
 for(const page of manifest.pages){
  if(!page.kind)errors.push(page.id+' kind missing');
  if(!manifest.groups.some(g=>g.id===page.group))errors.push(page.id+' group missing');
  if(!page.coverage?.length)errors.push(page.id+' substantive coverage missing');
  for(const l of ['ru','en'])if(!page.titles?.[l])errors.push(page.id+' title missing '+l);
 }
 for(const group of manifest.groups)for(const l of ['ru','en'])if(!group.titles?.[l])errors.push('group title missing '+group.id+' '+l);
 for(const old of baseline.pages){
  if(!ids.has(old.id))errors.push('removed baseline page '+old.id);
  for(const l of ['ru','en'])for(const anchor of old.anchors?.[l]??[])if(!files[l+'/'+old.id+'.md']?.includes('{#'+anchor+'}'))errors.push('removed baseline anchor '+old.id+' '+l+' '+anchor);
 }
 for(const file of Object.keys(files)){const id=file.slice(3,-3);if(!ids.has(id))errors.push('orphan '+file);}
 return errors;
}
export function validatePages(manifest, files) {
 const errors=[], ids=new Set();
 for(const page of manifest.pages) {
  if(ids.has(page.id)) errors.push('duplicate '+page.id);
  ids.add(page.id);
  if(page.kind && !['portal','guide','explanation','reference','faq','glossary'].includes(page.kind)) errors.push(page.id+' unknown kind');
  const topics=new Set();
  for(const topic of page.coverage??[]) {
   if(topics.has(topic.id)) errors.push(page.id+' duplicate coverage '+topic.id);
   topics.add(topic.id);
   if(!topic.sources?.length) errors.push(page.id+' coverage source missing '+topic.id);
   for(const locale of ['ru','en']) {
    if(!topic.questions?.[locale]) errors.push(page.id+' coverage question missing '+locale);
    const text=files[locale+'/'+page.id+'.md']??'', marker='{#'+topic.id+'}';
    if(!text.includes(marker)) errors.push(page.id+' coverage anchor missing '+topic.id+' '+locale);
    else {const body=text.slice(text.indexOf(marker)+marker.length).split(String.fromCharCode(10)).slice(1).join(String.fromCharCode(10)).split('## ')[0].trim();if(!body) errors.push(page.id+' coverage body empty '+topic.id+' '+locale);}
   }
  }
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
