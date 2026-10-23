import fs from 'node:fs/promises';
import path from 'node:path';
import { portablePath } from './bundle.mjs';

export function directoryReader(directory) {
  const root = path.resolve(directory);
  return async relative => {
    if (!portablePath(relative)) throw new Error('无效包内路径。');
    let current = root;
    for (const segment of relative.split('/')) {
      current = path.join(current, segment);
      if ((await fs.lstat(current)).isSymbolicLink()) throw new Error('资料成员不能是链接。');
    }
    return fs.readFile(current);
  };
}
