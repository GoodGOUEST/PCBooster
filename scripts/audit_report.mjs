// UI 重写轮：统一审计脚本（输出到文件，避免控制台重定向问题）
import fs from 'node:fs';
import path from 'node:path';

const ROOT = process.cwd();
const SKIP = new Set(['obj', 'bin', 'out', '.vs', '.git', 'node_modules', '.idea']);
const OUT = path.join(ROOT, 'audit_report.txt');
const lines = [];
const log = (s) => lines.push(s);

function walk(dir, acc = []) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    if (e.isDirectory()) { if (!SKIP.has(e.name)) walk(path.join(dir, e.name), acc); }
    else if (e.name.endsWith('.cs')) acc.push(path.join(dir, e.name));
  }
  return acc;
}

const files = walk(ROOT).map((f) => {
  const buf = fs.readFileSync(f);
  let text = '';
  let utf8 = true;
  try { text = new TextDecoder('utf-8', { fatal: true }).decode(buf); } catch { utf8 = false; }
  const bom = buf.length >= 3 && buf[0] === 0xef && buf[1] === 0xbb && buf[2] === 0xbf;
  return { f: path.relative(ROOT, f).replace(/\\/g, '/'), text, utf8, bom, bytes: buf.length };
});

const totalLines = files.reduce((a, x) => a + x.text.split('\n').length, 0);
log(`files=${files.length} lines=${totalLines}`);
log(`enc_utf8=${files.filter((x) => x.utf8).length} enc_bad=${files.filter((x) => !x.utf8).map((x) => x.f).join(',') || 'none'}`);
log(`bom=${files.filter((x) => x.bom).length}`);
log(`dllimports=${files.reduce((a, x) => a + (x.text.match(/\[DllImport/g) || []).length, 0)}`);
log(`todos=${files.reduce((a, x) => a + (x.text.match(/TODO|FIXME|HACK/g) || []).length, 0)}`);

// 括号平衡（去掉字符串/注释后的简易检查）
function strip(text) {
  let out = '';
  for (let i = 0; i < text.length; i++) {
    const c = text[i], n = text[i + 1];
    if (c === '/' && n === '/') { while (i < text.length && text[i] !== '\n') i++; out += '\n'; continue; }
    if (c === '/' && n === '*') { i += 2; while (i < text.length && !(text[i] === '*' && text[i + 1] === '/')) i++; i++; continue; }
    if (c === '"' || c === "'") { const q = c; i++; while (i < text.length) { if (text[i] === '\\') { i += 2; continue; } if (text[i] === q) break; i++; } continue; }
    out += c;
  }
  return out;
}
let bracesOk = 0;
const braceBad = [];
for (const x of files) {
  const s = strip(x.text);
  let depth = 0, bad = false;
  for (const ch of s) {
    if (ch === '{') depth++;
    else if (ch === '}') { depth--; if (depth < 0) { bad = true; break; } }
  }
  if (!bad && depth === 0) bracesOk++; else braceBad.push(x.f);
}
log(`braces_ok=${bracesOk}/${files.length} bad=${braceBad.join(',') || 'none'}`);

// 类型统计
const typeRe = /^\s*(?:public|internal|private|protected)?\s*(?:static\s+|sealed\s+|abstract\s+|partial\s+|readonly\s+)*(class|interface|struct|enum)\s+(\w+)/gm;
let types = 0;
const typeNames = new Map();
for (const x of files) {
  let m;
  while ((m = typeRe.exec(x.text))) { types++; typeNames.set(m[2], x.f); }
}
log(`types=${types}`);

// 死代码：public 成员在其它文件中出现次数为 0
const memberRe = /public\s+(?:static\s+|readonly\s+|const\s+|sealed\s+|virtual\s+|override\s+|async\s+|event\s+)*[\w<>\[\],\s?.]+?\s+(\w+)\s*(?:\(|\{|=>|;)/g;
const members = [];
for (const x of files) {
  let m;
  while ((m = memberRe.exec(x.text))) {
    const name = m[1];
    if (['get', 'set', 'class', 'interface', 'struct', 'enum', 'override', 'this'].includes(name)) continue;
    if (name.length < 2) continue;
    members.push({ name, file: x.f });
  }
}
const dead = [];
for (const mem of members) {
  const re = new RegExp(`(^|[^\\w])${mem.name}([^\\w]|$)`);
  let hits = 0;
  for (const x of files) {
    if (x.f === mem.file) continue;
    if (re.test(x.text)) { hits++; break; }
    // 同文件其它位置的使用也算（声明本身已被排除在外）
  }
  if (hits === 0) {
    // 同文件内再查一次（排除声明行）
    const same = files.find((x) => x.f === mem.file);
    const occurrences = (same.text.match(new RegExp(`(^|[^\\w])${mem.name}([^\\w]|$)`, 'g')) || []).length;
    if (occurrences <= 1) dead.push(`${mem.name}@${mem.file}`);
  }
}
log(`public_members=${members.length} possibly_dead=${dead.length}`);
if (dead.length) dead.forEach((d) => log(`  dead: ${d}`));

// 依赖图结果（由 parse_cs3.mjs 产出）
try {
  const g = JSON.parse(fs.readFileSync(path.join(ROOT, 'cs_parse.json'), 'utf8'));
  log(`graph_files=${g.files.length} cycles=${g.cycles.length} violations=${g.violations.length}`);
  g.cycles.forEach((c) => log(`  cycle: ${c.join(' -> ')}`));
  log('violations:');
  g.violations.forEach((v) => log(`  ${v.from} -> ${v.to}`));
} catch (e) { log('graph: missing cs_parse.json'); }

fs.writeFileSync(OUT, lines.join('\r\n'), 'utf8');
console.log(lines.join('\n'));
