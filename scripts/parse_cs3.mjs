import fs from 'node:fs';
import path from 'node:path';

const ROOT = process.cwd();
const SKIP_DIRS = new Set(['obj', 'bin', '.vs', '.git', 'node_modules']);

function walk(dir, out = []) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    if (e.isDirectory()) { if (SKIP_DIRS.has(e.name)) continue; walk(path.join(dir, e.name), out); }
    else if (e.name.toLowerCase().endsWith('.cs')) out.push(path.join(dir, e.name));
  }
  return out;
}
function decode(buf) {
  try { return { text: new TextDecoder('utf-8', { fatal: true }).decode(buf), enc: 'utf8', bom: buf[0] === 0xef && buf[1] === 0xbb && buf[2] === 0xbf }; }
  catch { return { text: new TextDecoder('gbk').decode(buf), enc: 'gbk', bom: false }; }
}
function strip(text) {
  let out = ''; const n = text.length; let i = 0;
  while (i < n) {
    const c = text[i], c2 = text[i + 1];
    if (c === '/' && c2 === '/') { while (i < n && text[i] !== '\n') { out += ' '; i++; } continue; }
    if (c === '/' && c2 === '*') { i += 2; out += '  '; while (i < n && !(text[i] === '*' && text[i + 1] === '/')) { out += text[i] === '\n' ? '\n' : ' '; i++; } i += 2; out += '  '; continue; }
    if (c === '@' && c2 === '"') { i += 2; out += '  '; while (i < n) { if (text[i] === '"' && text[i + 1] === '"') { out += '  '; i += 2; continue; } if (text[i] === '"') { out += ' '; i++; break; } out += text[i] === '\n' ? '\n' : ' '; i++; } continue; }
    if ((c === '$' && c2 === '"') || ((c === '@' || c === '$') && c2 === '$')) { i += 2; out += '  '; while (i < n) { if (text[i] === '\\') { out += '  '; i += 2; continue; } if (text[i] === '"') { out += ' '; i++; break; } out += text[i] === '\n' ? '\n' : ' '; i++; } continue; }
    if (c === '"') { i++; out += ' '; while (i < n) { if (text[i] === '\\') { out += '  '; i += 2; continue; } if (text[i] === '"') { out += ' '; i++; break; } out += text[i] === '\n' ? '\n' : ' '; i++; } continue; }
    if (c === "'") { const close = text.indexOf("'", i + 1); if (close > -1 && close - i <= 8 && !text.slice(i + 1, close).includes('\n')) { out += ' '.repeat(close - i + 1); i = close + 1; continue; } }
    out += c; i++;
  }
  return out;
}
function matchBrace(s, open) { let d = 0; for (let i = open; i < s.length; i++) { if (s[i] === '{') d++; else if (s[i] === '}') { d--; if (d === 0) return i; } } return -1; }
function chunks(body) {
  const res = []; let start = 0;
  for (let i = 0; i < body.length; i++) {
    if (body[i] === ';') { res.push(body.slice(start, i + 1)); start = i + 1; continue; }
    if (body[i] === '{') { const close = matchBrace(body, i); if (close === -1) break; res.push(body.slice(start, close + 1)); start = close + 1; i = close; continue; }
  }
  const tail = body.slice(start); if (tail.trim()) res.push(tail);
  return res.map(c => c.trim()).filter(Boolean);
}
const CONTROL = new Set(['if', 'for', 'foreach', 'while', 'switch', 'catch', 'using', 'lock', 'return', 'else', 'do', 'try', 'fixed', 'sizeof', 'typeof', 'nameof', 'when', 'case', 'throw', 'new', 'base', 'this', 'default', 'checked', 'unchecked', 'stackalloc', 'await', 'yield', 'get', 'set', 'add', 'remove', 'init', 'where']);

const files = walk(ROOT).sort();
const parsed = [];

for (const f of files) {
  const buf = fs.readFileSync(f);
  const { text, enc, bom } = decode(buf);
  const s = strip(text);
  const rel = path.relative(ROOT, f).replace(/\\/g, '/');
  const usings = [...s.matchAll(/(?:^|\n)[ \t]*using\s+(?:static\s+)?([A-Za-z_][\w.]*)\s*;/g)].map(m => m[1]);
  const nsMatch = s.match(/(?:^|\n)[ \t]*namespace\s+([\w.]+)/);
  const types = [];
  const typeRe = /(?:^|\n)([ \t]*)((?:[\w]+\s+)*?)(class|struct|record|interface|enum)\s+([A-Za-z_]\w*)(\s*<[^>{;]*>)?\s*(\([^)]*\))?\s*(?::\s*([^{;\n]+))?\s*((?:where\b[^{;\n]*)?)\s*(\{|;)/g;
  let m;
  while ((m = typeRe.exec(s))) {
    const mods = m[2].trim().split(/\s+/).filter(Boolean);
    if (mods.some(x => CONTROL.has(x))) continue;
    const kind = m[3], name = m[4] + (m[5] || '').replace(/\s+/g, '');
    const generic = (m[5] || '').includes('<');
    const base = (m[7] || '').trim();
    const members = [];
    if (m[9] === '{') {
      const braceIdx = m.index + m[0].length - 1;
      const close = matchBrace(s, braceIdx);
      if (close > braceIdx) {
        const body = s.slice(braceIdx + 1, close);
        for (const ch of chunks(body)) {
          const one = ch.replace(/\s+/g, ' ').trim();
          if (!one || one.startsWith('#')) continue;
          if (kind === 'enum') {
            for (const part of one.replace(/[{};]/g, '').split(',')) {
              const v = part.trim().split('=')[0].trim();
              if (/^[A-Za-z_]\w*$/.test(v)) members.push({ kind: 'value', name: v, sig: part.trim() });
            }
            continue;
          }
          const head = one.split('{')[0].split('=>')[0].trim();
          // skip initializer fragments split off from properties/fields (e.g. "= 30;", "new();")
          if (!head || /^[=:]/.test(head) || /^(new|Array|Constants|Environment|Path|TimeSpan|DateTime)\b/.test(head)) continue;
          const nested = head.match(/^(?:(?:public|private|protected|internal|static|sealed|abstract|partial|readonly|unsafe|new)\s+)*(class|struct|record|interface|enum)\s+([A-Za-z_]\w*)/);
          let memKind, memName;
          const cm = head.match(/^((?:[\w@]+\s+|[\w@]+<[^>]*>\s+)*?)~?([A-Za-z_]\w*)\s*(<[^>]*>)?\s*\(/);
          if (nested) { memKind = 'nested-' + nested[1]; memName = nested[2]; }
          else if (cm && !/^(return|if|for|foreach|while|switch|lock|using|catch)\b/.test(head)) {
            const kw = cm[1].trim();
            memKind = kw.includes('event') ? 'event' : kw.includes('delegate') ? 'delegate' : 'method';
            memName = cm[2];
          } else {
            const last = head.split(/\s+/).filter(Boolean).pop() || '';
            memName = last.replace(/[;,]/g, '').replace(/<.*/, '');
            if (/\bevent\b/.test(head)) memKind = 'event';
            else if (/\bconst\b/.test(head)) memKind = 'const';
            else if (/\(/.test(head)) memKind = 'delegate';
            else if (/\bget\b|\bset\b|\binit\b|=>/.test(one)) memKind = 'property';
            else memKind = 'field';
            if (new RegExp(`^${name.replace(/<.*/, '')}$`, 'i').test(memName)) memKind = 'ctor';
          }
          members.push({ kind: memKind, name: memName, sig: head.slice(0, 140) });
        }
      }
    }
    types.push({ kind, name, generic, base, members });
  }

  const dllimports = [...text.matchAll(/DllImport\(\s*"([^"]+)"/g)].map(x => x[1]);
  const todos = [...text.matchAll(/\/\/[^\n]*?\b(TODO|FIXME|HACK|XXX|BUG)\b[:：]?\s*([^\n]*)/g)].map(x => ({ tag: x[1], text: x[2].trim().slice(0, 120) }));

  parsed.push({
    file: rel, enc, bom, bytes: buf.length, lines: text.split('\n').length,
    namespace: nsMatch ? nsMatch[1] : null, usings, types, dllimports, todos,
    commentLines: text.split('\n').filter(l => /^\s*(\/\/|\/\*|\*)/.test(l)).length,
    stars: (text.match(/★/g) || []).length,
  });
}

const typeOwner = new Map();
for (const p of parsed) for (const t of p.types) typeOwner.set(t.name.replace(/<.*/, ''), p.file);
const edges = [];
const refCount = new Map();
for (const p of parsed) {
  let s = strip(decode(fs.readFileSync(path.join(ROOT, p.file))).text);
  // neutralize const/field declaration names that collide with type names (e.g. ModuleRegistry.Names.X)
  s = s.replace(/\bconst\s+string\s+[A-Za-z_]\w*/g, 'const string _');
  const to = new Set();
  for (const [tname, owner] of typeOwner) {
    if (owner === p.file || tname.length < 3) continue;
    if (new RegExp(`(^|[^\\w.])${tname}([^\\w]|$)`).test(s)) { to.add(owner); refCount.set(owner, (refCount.get(owner) || 0) + 1); }
  }
  edges.push({ from: p.file, to: [...to].sort() });
}
const g = new Map(edges.map(e => [e.from, e.to]));
const cycles = [];
const state = new Map();
function dfs(v, stack) {
  state.set(v, 1); stack.push(v);
  for (const w of g.get(v) || []) {
    if (!g.has(w)) continue;
    if (state.get(w) === 1) { const cyc = stack.slice(stack.indexOf(w)); if (cyc.length > 1 && !cycles.some(c => c.length === cyc.length && c.every(x => cyc.includes(x)))) cycles.push(cyc); }
    else if (!state.get(w)) dfs(w, stack);
  }
  stack.pop(); state.set(v, 2);
}
for (const v of g.keys()) if (!state.get(v)) dfs(v, []);

const layerOf = f => f.includes('/') ? f.split('/')[0] : '(root)';
const order = { Infrastructure: 0, Execution: 1, Core: 2, Intelligence: 2, UI: 3, '(root)': 4 };
const violations = [];
for (const e of edges) for (const t of e.to) {
  const a = layerOf(e.from), b = layerOf(t);
  if (order[b] > order[a]) violations.push({ from: e.from, to: t });
}
const dupNames = {};
for (const p of parsed) for (const t of p.types) { const n = t.name.replace(/<.*/, ''); (dupNames[n] = dupNames[n] || []).push(p.file); }

fs.writeFileSync('cs_parse.json', JSON.stringify({ root: ROOT, files: parsed, edges, cycles, violations, dupNames }, null, 0));
console.log(`files=${parsed.length} lines=${parsed.reduce((a, p) => a + p.lines, 0)} types=${parsed.reduce((a, p) => a + p.types.length, 0)}`);
console.log(`enc=${JSON.stringify(parsed.reduce((a, p) => (a[p.enc] = (a[p.enc] || 0) + 1, a), {}))} bom=${parsed.filter(p => p.bom).length}`);
console.log(`dllimports=${parsed.reduce((a, p) => a + p.dllimports.length, 0)} todos=${parsed.reduce((a, p) => a + p.todos.length, 0)}`);
console.log('ZERO-TYPE:', parsed.filter(p => p.types.length === 0).map(p => p.file).join(', ') || 'none');
console.log('DUP:', Object.entries(dupNames).filter(([k, v]) => v.length > 1).map(([k, v]) => k + ':' + v.length).join(', ') || 'none');
console.log('CYCLES:', cycles.length);
console.log('VIOLATIONS:', violations.length);
console.log('REFCOUNT:', [...refCount.entries()].sort((a, b) => b[1] - a[1]).slice(0, 10).map(x => x.join('=')).join(', '));
console.log('TODOS:'); parsed.filter(p => p.todos.length).forEach(p => p.todos.forEach(t => console.log('  ', p.file, t.tag, t.text)));
