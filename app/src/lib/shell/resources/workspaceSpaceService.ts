import { resourceService } from './resourceService.ts';
import type { ResourceDiskRoot } from './resourceTypes.ts';

export const workspaceSpaceService = {
  scan(roots: ResourceDiskRoot[]) {
    return resourceService.readDisk(roots, { maxDepth: 3, maxEntries: 2000 });
  },
  async cancel(): Promise<void> {
    await resourceService.cancelDisk();
  }
};
