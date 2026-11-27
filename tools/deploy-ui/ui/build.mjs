import * as esbuild from 'esbuild';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const templatePath = path.join(__dirname, 'template.html');
const outPath = path.resolve(__dirname, '..', 'app.html');

const result = await esbuild.build({
  entryPoints: [path.join(__dirname, 'src', 'main.ts')],
  bundle: true,
  write: false,
  format: 'iife',
  target: ['es2022'],
  minify: true,
});

const js = result.outputFiles[0].text;
// Validate JS syntax before embedding.
new Function(js);
if (/<\/script/i.test(js)) {
  throw new Error('Bundled JS contains a literal </script tag');
}
const template = fs.readFileSync(templatePath, 'utf-8');
// Use a replacer function so $&, $', $$ in minified JS are never interpreted.
const html = template.replace('<!--__INLINE_SCRIPT__-->', () => `<script>\n${js}\n</script>`);
fs.writeFileSync(outPath, html, 'utf-8');
console.log(`Built ${outPath} (${(Buffer.byteLength(html) / 1024).toFixed(1)} KiB)`);
