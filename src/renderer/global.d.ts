// preload が contextBridge で公開する API を renderer 側から見えるようにする。

import type { RendererApi } from '../shared/types';

declare global {
  interface Window {
    api: RendererApi;
  }
}

export {};
