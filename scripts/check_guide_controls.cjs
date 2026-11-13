const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const html = fs.readFileSync('사용안내_v0.1.19.html','utf8');
const code = html.match(/<script>([\s\S]*?)<\/script>/)[1];
function run(stored, blocked=false, dark=false) {
  const elements = {};
  const get = id => elements[id] ||= {value:'',hidden:true,addEventListener(type, fn){this[type]=fn;}};
  const root={dataset:{},style:{}};
  let saved=stored;
  vm.runInNewContext(code, {document:{documentElement:root,getElementById:get,querySelector:()=>get('tools')},window:{matchMedia:()=>({matches:dark})},localStorage:{getItem(){if(blocked)throw Error();return saved;},setItem(k,v){if(blocked)throw Error();saved=v;}}});
  return {get,root,stored:()=>saved};
}
let app=run(null);
assert.equal(app.root.dataset.theme,'light');
assert.equal(app.root.style.fontSize,'16px');
assert.equal(app.get('tools').hidden,false);
app.get('guide-theme').value='dark'; app.get('guide-theme').change();
assert.equal(app.root.dataset.theme,'dark');
for(let n=14;n<=22;n++) {app.get('guide-size').value=String(n);app.get('guide-size').input();assert.equal(app.root.style.fontSize,n+'px');}
const restored=run(app.stored());
assert.equal(restored.root.dataset.theme,'dark');assert.equal(restored.root.style.fontSize,'22px');
app.get('guide-reset').click();assert.equal(app.root.dataset.theme,'light');assert.equal(app.root.style.fontSize,'16px');
assert.equal(run(null,false,true).root.dataset.theme,'dark');
for(const bad of ['broken','null','{"size":100,"theme":"bad"}'])assert.equal(run(bad).root.style.fontSize,'16px');
app=run(null,true);app.get('guide-theme').value='dark';app.get('guide-theme').change();assert.match(app.get('guide-status').textContent,/이번 열람/);
console.log('PASS: themes, all font sizes, persistence, reset, system default, corrupt/blocked storage. JS logic tested in Node DOM stub; browser rendering not tested.');
