// ビルドスクリプト: esbuild で main / preload / renderer の 3 バンドルを作り、
// 静的ファイル(HTML/CSS/フォント)を dist へコピーする。
//
//   node build.mjs           1 回だけビルド
//   node build.mjs --watch   変更を監視して再ビルド

import * as esbuild from 'esbuild';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.dirname(fileURLToPath(import.meta.url));
const dist = path.join(root, 'dist');
const watch = process.argv.includes('--watch');

/** main / preload は Node(Electron) 側、renderer は Chromium 側で動く。 */
const configs = [
  {
    entryPoints: [path.join(root, 'src/main/main.ts')],
    outfile: path.join(dist, 'main/main.js'),
    platform: 'node',
    target: 'node22',
    format: 'cjs',
    // electron 本体とネイティブモジュールはバンドルせず実行時に require する。
    external: ['electron', '@lydell/node-pty'],
  },
  {
    entryPoints: [path.join(root, 'src/preload/preload.ts')],
    outfile: path.join(dist, 'preload/preload.js'),
    platform: 'node',
    target: 'node22',
    format: 'cjs',
    external: ['electron'],
  },
  {
    entryPoints: [path.join(root, 'src/renderer/renderer.ts')],
    outfile: path.join(dist, 'renderer/renderer.js'),
    platform: 'browser',
    target: 'chrome130',
    format: 'iife',
  },
].map((c) => ({ ...c, bundle: true, sourcemap: true, logLevel: 'info' }));

/** 静的ファイルのコピー元 → コピー先。 */
const staticFiles = [
  ['src/renderer/index.html', 'renderer/index.html'],
  ['src/renderer/style.css', 'renderer/style.css'],
  ['assets/CascadiaMono-Regular.ttf', 'renderer/assets/CascadiaMono-Regular.ttf'],
];

function copyStatic() {
  for (const [from, to] of staticFiles) {
    const src = path.join(root, from);
    const dest = path.join(dist, to);
    fs.mkdirSync(path.dirname(dest), { recursive: true });
    fs.copyFileSync(src, dest);
  }
}

copyStatic();

if (watch) {
  for (const config of configs) {
    const ctx = await esbuild.context(config);
    await ctx.watch();
  }
  // 静的ファイルも監視する(esbuild の対象外なので自前で)。
  for (const [from] of staticFiles) {
    fs.watchFile(path.join(root, from), { interval: 300 }, copyStatic);
  }
  console.log('watching...');
} else {
  await Promise.all(configs.map((c) => esbuild.build(c)));
}
