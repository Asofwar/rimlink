import {chromium} from 'playwright';
import {createServer} from 'node:http';
import {readFileSync,existsSync,mkdirSync} from 'node:fs';
import {resolve,extname} from 'node:path';
import assert from 'node:assert/strict';
const catalogCount=JSON.parse(readFileSync(resolve(import.meta.dirname,'../content-manifest.json'))).pages.length;
const dist=resolve(import.meta.dirname,'../.vitepress/dist'), output=resolve(import.meta.dirname,'../.test-results');
mkdirSync(output,{recursive:true});
const server=createServer((req,res)=>{
 const path=decodeURIComponent(new URL(req.url,'http://localhost').pathname);
 if(!path.startsWith('/rimlink/')){res.writeHead(404).end();return;}
 let file=resolve(dist,path.slice(9));
 if(file!==dist&&!file.startsWith(dist+'/')&&!file.startsWith(dist+String.fromCharCode(92))){res.writeHead(403).end();return;}
 if(existsSync(file+'.html'))file+='.html';else if(existsSync(resolve(file,'index.html')))file=resolve(file,'index.html');
 if(!existsSync(file)){res.statusCode=404;file=resolve(dist,'404.html');}
 res.setHeader('Content-Type',({'.html':'text/html; charset=utf-8','.js':'text/javascript','.css':'text/css','.json':'application/json','.woff2':'font/woff2','.svg':'image/svg+xml'})[extname(file)]??'application/octet-stream');
 try{res.end(readFileSync(file));}catch{res.writeHead(404).end();}
});
await new Promise(r=>server.listen(0,'127.0.0.1',r));
const origin='http://127.0.0.1:'+server.address().port, base=origin+'/rimlink/';
let browser;const errors=[],external=[],checks=[];
try{
 browser=await chromium.launch({headless:true,...(process.env.WIKI_BROWSER_CHANNEL?{channel:process.env.WIKI_BROWSER_CHANNEL}:{})});
 const ctx=await browser.newContext({viewport:{width:1280,height:900}}),page=await ctx.newPage();
 page.on('pageerror',e=>errors.push(e.message));page.on('request',r=>{if(!r.url().startsWith(origin))external.push(r.url());});
 page.setDefaultTimeout(10000);
 await page.goto(base);assert.equal(await page.locator('h1').count(),1);await page.getByRole('link',{name:'Русский',exact:true}).click();
 await page.locator('#local-search button').waitFor();
 assert.equal(await page.locator('.wiki-portal .portal-route').count(),3,'three actionable portal routes');
 assert.equal(await page.locator('.wiki-portal .catalog-link').count(),catalogCount,'complete manifest catalog');
 await page.setViewportSize({width:1440,height:900});
 assert(await page.locator('.wiki-portal').evaluate(e=>e.getBoundingClientRect().width>=1100),'portal uses wide desktop layout');
 const cases={ru:[['аукционы','players/auctions'],['закупки','players/procurement'],['живая','players/live'],['гильдии','players/guild-influence'],['сертификат','admins/security-auth'],['резервные','admins/backup-recovery'],['диагностика','players/troubleshooting'],['установка','players/install'],['чат','players/chat-pings'],['конфигурация','admins/configuration']],en:[['auctions','players/auctions'],['procurement','players/procurement'],['live','players/live'],['influence','players/guild-influence'],['authentication','admins/security-auth'],['backup','admins/backup-recovery'],['troubleshooting','players/troubleshooting'],['install','players/install'],['chat','players/chat-pings'],['configuration','admins/configuration']]};
 const search=async query=>{await page.locator('#local-search button').click();await page.locator('#localsearch-input').fill(query);await page.waitForFunction(()=>document.querySelectorAll('.VPLocalSearchBox .result').length>0);return page.locator('.VPLocalSearchBox .result').evaluateAll(els=>els.map(e=>e.getAttribute('href')));};
 for(const lang of ['ru','en']) {
  await page.goto(base+lang+'/');
  for(const [query,target] of cases[lang]){
   const results=await search(query);
   assert(results.every(r=>r.includes('/'+lang+'/')),'cross-language result '+query);
   assert(results.slice(0,5).some(r=>r.includes('/'+target)),query+' expected '+target+' top5: '+results.slice(0,5));
   checks.push(lang+' search '+query);await page.keyboard.press('Escape');await page.locator('#localsearch-input').waitFor({state:'hidden'});
  }
  await page.locator('#local-search button').click();await page.locator('#localsearch-input').fill('qzxnonexistent6732');await page.locator('.no-results').waitFor();await page.keyboard.press('Escape');
 }
 await page.goto(base+'ru/');
 const folded=[];for(const query of ['кошелёк','кошелек','КОШЕЛЁК']){folded.push(await search(query));await page.keyboard.press('Escape');}
 assert.deepEqual(folded[0],folded[1]);assert.deepEqual(folded[0],folded[2]);checks.push('Cyrillic ё/е and case folding');
 await page.goto(base+'ru/players/live#limits');await page.locator('.VPNavBarTranslations').hover();
 await page.locator('.VPNavBarTranslations').getByRole('link',{name:'English',exact:true}).click();await page.waitForURL('**/en/players/live#limits');
 await page.reload();assert((await page.locator('h1').innerText()).includes('Live'));
 await page.keyboard.press('Control+k');await page.locator('#localsearch-input').fill('auctions');await page.locator('.result').first().waitFor();await page.keyboard.press('ArrowDown');await page.keyboard.press('Enter');await page.locator('#localsearch-input').waitFor({state:'hidden'});assert(page.url().includes('/en/players/auctions'));
 checks.push('same-page language switch + hash, deep reload, keyboard search navigation, no results + Escape');
 for(const lang of ['ru','en'])for(const width of [360,768,1440])for(const dark of [false,true]){
  await page.setViewportSize({width,height:900});await page.goto(base+lang+'/admins/configuration');
  await page.evaluate(d=>document.documentElement.classList.toggle('dark',d),dark);
  assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),'horizontal page overflow '+width);
  const contrast=await page.evaluate(()=>{const c=getComputedStyle(document.querySelector('.vp-doc p')),b=getComputedStyle(document.documentElement).getPropertyValue('--vp-c-bg');return {text:c.color,bg:b};});
  const rgb=contrast.text.match(/[0-9.]+/g).slice(0,3).map(Number),hex=contrast.bg.trim().slice(1),background=[0,2,4].map(i=>parseInt(hex.slice(i,i+2),16));
  const luminance=values=>values.map(v=>v/255).map(v=>v<=0.04045?v/12.92:((v+0.055)/1.055)**2.4).reduce((sum,v,i)=>sum+v*[0.2126,0.7152,0.0722][i],0);
  const a=luminance(rgb),b=luminance(background),ratio=(Math.max(a,b)+0.05)/(Math.min(a,b)+0.05);assert(ratio>=4.5,'body text contrast '+ratio);
  checks.push(lang+' '+width+' '+(dark?'dark':'light')+' body contrast '+ratio.toFixed(2));
  await page.screenshot({path:resolve(output,lang+'-'+width+'-'+(dark?'dark':'light')+'.png'),fullPage:false,animations:'disabled'});
 }
 for(const lang of ['ru','en'])for(const [article,asset] of [['','world'],['players/connect','connect'],['players/economy','exchange'],['players/live','live']]){
  await page.setViewportSize({width:1280,height:900});await page.goto(base+lang+'/'+article);
  const image=page.locator('img.wiki-art');await image.waitFor();await image.evaluate(img=>img.decode());
  assert(await image.evaluate(img=>img.naturalWidth>0&&img.alt.length>15&&img.currentSrc.includes('/rimlink/illustrations/')),'illustration/base/alt '+asset);
  for(const width of [360,768,1440])for(const dark of [false,true]){
   await page.setViewportSize({width,height:900});await page.evaluate(d=>document.documentElement.classList.toggle('dark',d),dark);
   assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),'art overflow '+asset);
   await page.screenshot({path:resolve(output,lang+'-'+asset+'-'+width+'-'+(dark?'dark':'light')+'.png'),animations:'disabled'});
  }
  checks.push(lang+' illustration '+asset+' loaded, localized, responsive');
 }
 await page.setViewportSize({width:360,height:900});await page.goto(base+'ru/');await page.locator('.VPNavBarHamburger').click();await page.locator('.VPNavScreen').getByRole('link',{name:'Игрокам',exact:true}).click();assert(page.url().includes('/ru/players/getting-started'));
 // 640 CSS pixels is the reflow width of a 1280px desktop viewport at 200% browser zoom.
 await page.setViewportSize({width:640,height:450});await page.goto(base+'en/admins/api');assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),'200% zoom reflow');checks.push('mobile menu and 200% desktop reflow');
 for(const lang of ['ru','en']){
  await page.setViewportSize({width:1440,height:900});await page.goto(base+lang+'/');assert.equal(await page.locator('.catalog-link').count(),catalogCount);
  assert(await page.locator('#catalog-title').evaluate(e=>e.getBoundingClientRect().top<900),'catalog starts above fold');
  for(const width of [360,768,1440])for(const dark of [false,true]){await page.setViewportSize({width,height:900});await page.evaluate(d=>document.documentElement.classList.toggle('dark',d),dark);assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),'portal overflow');await page.screenshot({path:resolve(output,'portal-'+lang+'-'+width+'-'+(dark?'dark':'light')+'.png'),animations:'disabled'});}
  for(const id of ['players/getting-started','players/gifts-trades','players/live','players/coop','players/modpacks','admins/backup-recovery','developers/build']){await page.goto(base+lang+'/'+id);assert((await page.locator('main h2').count())>=4,'substantive guide '+id);assert(await page.locator('.VPSidebarItem.is-active').count()>0,'active sidebar '+id);}
 }
 const nojs=await browser.newContext({javaScriptEnabled:false}),np=await nojs.newPage();for(const lang of ['ru','en']){await np.goto(base+lang+'/');assert.equal(await np.locator('.catalog-link').count(),catalogCount,'SSR catalog');assert.equal(await np.locator('.portal-route').count(),3);await np.goto(base+lang+'/players/install');assert((await np.locator('main').innerText()).includes('Harmony'));}await nojs.close();checks.push('portal catalog, above-fold routes, active sidebar, 7 deep guides, no-JS RU/EN');
 const notfound=await page.goto(base+'missing-page');assert.equal(notfound.status(),404);assert((await page.locator('.NotFound').innerText()).includes('404'));checks.push('404');
 assert.deepEqual(errors,[]);assert.deepEqual(external,[]);
 checks.forEach(c=>console.log('PASS '+c));console.log('No runtime errors; no external requests (including search). Screenshots: .test-results/');
} finally {await browser?.close();await new Promise(r=>server.close(r));}
