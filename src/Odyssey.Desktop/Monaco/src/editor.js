import * as monaco from 'monaco-editor/esm/vs/editor/editor.api.js';
import 'monaco-editor/esm/vs/language/json/monaco.contribution.js';
import 'monaco-editor/esm/vs/language/css/monaco.contribution.js';
import 'monaco-editor/esm/vs/language/html/monaco.contribution.js';
import 'monaco-editor/esm/vs/language/typescript/monaco.contribution.js';
import 'monaco-editor/esm/vs/basic-languages/cpp/cpp.contribution.js';
import 'monaco-editor/esm/vs/basic-languages/csharp/csharp.contribution.js';
import 'monaco-editor/esm/vs/basic-languages/go/go.contribution.js';
import 'monaco-editor/esm/vs/basic-languages/ini/ini.contribution.js';
import 'monaco-editor/esm/vs/basic-languages/java/java.contribution.js';
import 'monaco-editor/esm/vs/basic-languages/kotlin/kotlin.contribution.js';
import 'monaco-editor/esm/vs/basic-languages/markdown/markdown.contribution.js';
import 'monaco-editor/esm/vs/basic-languages/powershell/powershell.contribution.js';
import 'monaco-editor/esm/vs/basic-languages/python/python.contribution.js';
import 'monaco-editor/esm/vs/basic-languages/rust/rust.contribution.js';
import 'monaco-editor/esm/vs/basic-languages/shell/shell.contribution.js';
import 'monaco-editor/esm/vs/basic-languages/sql/sql.contribution.js';
import 'monaco-editor/esm/vs/basic-languages/swift/swift.contribution.js';
import 'monaco-editor/esm/vs/basic-languages/xml/xml.contribution.js';
import 'monaco-editor/esm/vs/basic-languages/yaml/yaml.contribution.js';

self.MonacoEnvironment = {
  getWorkerUrl(_moduleId, label) {
    if (label === 'json') return './json.worker.js';
    if (label === 'css' || label === 'scss' || label === 'less') return './css.worker.js';
    if (label === 'html' || label === 'handlebars' || label === 'razor') return './html.worker.js';
    if (label === 'typescript' || label === 'javascript') return './typescript.worker.js';
    return './editor.worker.js';
  }
};

const send = message => {
  if (typeof globalThis.invokeCSharpAction === 'function')
    globalThis.invokeCSharpAction(JSON.stringify(message));
};

const editor = monaco.editor.create(document.getElementById('editor'), {
  automaticLayout: true,
  detectIndentation: true,
  fontFamily: "'Cascadia Mono','JetBrains Mono',monospace",
  fontSize: 13,
  minimap: { enabled: true },
  renderWhitespace: 'selection',
  scrollBeyondLastLine: false,
  smoothScrolling: true,
  theme: 'vs-dark',
  wordWrap: 'off'
});

let model = null;
let changeSubscription = null;
let savedVersion = 0;
let dirty = false;

function reportDirty(next) {
  if (dirty === next) return;
  dirty = next;
  send({ type: 'dirty', dirty });
}

function openDocument(payload) {
  changeSubscription?.dispose();
  model?.dispose();
  const safeName = encodeURIComponent(payload.name || 'document.txt');
  model = monaco.editor.createModel(
    payload.content || '',
    payload.languageId || 'plaintext',
    monaco.Uri.parse(`inmemory://odyssey/${safeName}`));
  editor.setModel(model);
  editor.updateOptions({ readOnly: Boolean(payload.readOnly), domReadOnly: Boolean(payload.readOnly) });
  savedVersion = model.getAlternativeVersionId();
  dirty = false;
  changeSubscription = model.onDidChangeContent(() =>
    reportDirty(model.getAlternativeVersionId() !== savedVersion));
  editor.focus();
}

function setReadOnly(value) {
  editor.updateOptions({ readOnly: Boolean(value), domReadOnly: Boolean(value) });
}

function closeDocument() {
  changeSubscription?.dispose();
  changeSubscription = null;
  editor.setModel(null);
  model?.dispose();
  model = null;
  savedVersion = 0;
  dirty = false;
}

function requestContent(requestId) {
  send({ type: 'content', requestId, content: model?.getValue() ?? '' });
}

function markSaved() {
  if (!model) return;
  savedVersion = model.getAlternativeVersionId();
  reportDirty(false);
}

editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => send({ type: 'save' }));
editor.addCommand(monaco.KeyCode.Escape, () => send({ type: 'close' }));
window.addEventListener('keydown', event => {
  if (/^F[2-8]$/.test(event.key)) {
    event.preventDefault();
    event.stopPropagation();
  }
}, true);

globalThis.odysseyEditor = {
  openDocument, closeDocument, setReadOnly, requestContent, markSaved, focus: () => editor.focus()
};
send({ type: 'ready' });
