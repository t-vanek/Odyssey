import { build } from 'esbuild';
import { createHash } from 'node:crypto';
import { cp, mkdir, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.dirname(fileURLToPath(import.meta.url));
const output = path.join(root, 'dist');
await rm(output, { recursive: true, force: true });
await mkdir(output, { recursive: true });

await build({
  entryPoints: [path.join(root, 'src/editor.js')],
  bundle: true,
  format: 'esm',
  target: ['chrome110', 'safari16'],
  outfile: path.join(output, 'editor.js'),
  loader: { '.ttf': 'file' },
  assetNames: 'assets/[name]-[hash]',
  legalComments: 'eof',
  minify: true
});

const workers = {
  'editor.worker.js': 'monaco-editor/esm/vs/editor/editor.worker.js',
  'json.worker.js': 'monaco-editor/esm/vs/language/json/json.worker.js',
  'css.worker.js': 'monaco-editor/esm/vs/language/css/css.worker.js',
  'html.worker.js': 'monaco-editor/esm/vs/language/html/html.worker.js',
  'typescript.worker.js': 'monaco-editor/esm/vs/language/typescript/ts.worker.js'
};
for (const [name, entry] of Object.entries(workers)) {
  await build({
    entryPoints: [entry],
    bundle: true,
    format: 'esm',
    target: ['chrome110', 'safari16'],
    outfile: path.join(output, name),
    legalComments: 'eof',
    minify: true
  });
}

await cp(path.join(root, 'src/index.html'), path.join(output, 'index.html'));
await cp(path.join(root, 'node_modules/monaco-editor/LICENSE'), path.join(output, 'LICENSE.monaco.txt'));
await cp(
  path.join(root, 'node_modules/monaco-editor/ThirdPartyNotices.txt'),
  path.join(output, 'ThirdPartyNotices.monaco.txt'));

async function filesBelow(directory, prefix = '') {
  const result = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const relative = path.posix.join(prefix, entry.name);
    if (entry.isDirectory()) result.push(...await filesBelow(path.join(directory, entry.name), relative));
    else result.push(relative);
  }
  return result.sort();
}

const assets = {};
for (const relative of await filesBelow(output)) {
  const content = await readFile(path.join(output, relative));
  assets[relative] = {
    bytes: content.length,
    sha256: createHash('sha256').update(content).digest('hex').toUpperCase()
  };
}
await writeFile(
  path.join(output, 'asset-manifest.json'),
  `${JSON.stringify({ schemaVersion: 1, assets }, null, 2)}\n`,
  'utf8');
