export function validateDrift(manifest,changed,exceptions=[],context={}) {
 const errors=[];
 const approved=(pageId,hits)=>exceptions.some(e=>e.pageId===pageId&&e.reason?.trim().length>=30&&e.reviewer?.trim()&&e.base===context.base&&context.base&&context.exceptionChanged&&e.sources?.length===hits.length&&hits.every(f=>e.sources.includes(f)&&e.after?.[f]===context.digests?.[f]));
 const uncovered=changed.filter(f=>(manifest.fallbackSourcePrefixes??[]).some(p=>f.startsWith(p))&&!manifest.pages.some(page=>page.sourcePaths.some(p=>f===p||f.startsWith(p.endsWith('/')?p:p+'/'))));
 if(uncovered.length&&!approved('unmapped',uncovered))errors.push('Unmapped sources: assign feature-specific pages in content-manifest or provide current reviewed unmapped rationale: '+uncovered.join(', '));
 for(const page of manifest.pages) {
  const hits=changed.filter(f=>page.sourcePaths.some(p=>f===p||f.startsWith(p.endsWith('/')?p:p+'/')));
  const edits=['ru','en'].map(l=>'wiki/'+l+'/'+page.id+'.md').filter(f=>changed.includes(f));
  if(edits.length===1&&!approved(page.id,edits))errors.push(page.id+': one-sided translation edit; update both locales or review a digest-bound typo exception');
  if(hits.length&&edits.length!==2&&!approved(page.id,hits))errors.push(page.id+': update RU + EN for '+hits.join(', ')+' or provide a current reviewed digest-bound rationale');
 }
 return errors;
}
