// preload: renderer から使える API を contextBridge 経由で公開する。
// renderer 側では Node API を直接使わせない(contextIsolation 有効)。

import { contextBridge, ipcRenderer, webUtils } from 'electron';
import type { Bootstrap, LaunchConfig, RendererApi } from '../shared/types';

const api: RendererApi = {
  bootstrap: () => ipcRenderer.invoke('app:bootstrap') as Promise<Bootstrap>,
  newTabLaunch: () =>
    ipcRenderer.invoke('app:newTabLaunch') as Promise<LaunchConfig | null>,
  saveMemos: (text) => ipcRenderer.invoke('dialog:saveMemos', text) as Promise<boolean>,
  clipboardImage: () => ipcRenderer.invoke('clipboard:image') as Promise<string | null>,
  copyText: (text) => ipcRenderer.send('clipboard:copy', text),

  beep: () => ipcRenderer.send('app:beep'),
  flash: () => ipcRenderer.send('app:flash'),
  closeWindow: () => ipcRenderer.send('app:close'),

  // Electron 32 以降 File.path が削除されたため、D&D のパス取得はこの API を使う。
  getPathForFile: (file: File) => webUtils.getPathForFile(file),

  pty: {
    spawn: (launch: LaunchConfig, cols: number, rows: number) =>
      ipcRenderer.invoke('pty:spawn', launch, cols, rows) as Promise<number>,
    write: (id, data) => ipcRenderer.send('pty:write', id, data),
    resize: (id, cols, rows) => ipcRenderer.send('pty:resize', id, cols, rows),
    kill: (id) => ipcRenderer.send('pty:kill', id),
    onData: (cb) => {
      ipcRenderer.on('pty:data', (_e, id: number, data: string) => cb(id, data));
    },
    onExit: (cb) => {
      ipcRenderer.on('pty:exit', (_e, id: number) => cb(id));
    },
  },
};

contextBridge.exposeInMainWorld('api', api);
