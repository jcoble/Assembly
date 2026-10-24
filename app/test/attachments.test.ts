import assert from 'node:assert/strict';
import { test } from 'node:test';
import { baseName, formatBytes, rejection, splitAttachments, thumbnailType, withAttachments, insertAt, pathsText } from '../src/lib/composer/attachments.ts';

const shot = { path: '/home/u/spike-sandboxes/attachments/t1/a.png', name: 'Screen Shot (2).png' };
const photo = { path: '/home/u/spike-sandboxes/attachments/t1/b.jpg', name: 'photo.jpg' };

test('the sent message carries one reference line per attachment after the text', () => {
  assert.equal(
    withAttachments('look at these', [shot, photo]),
    'look at these\n\n' +
      'Attached image: /home/u/spike-sandboxes/attachments/t1/a.png (Screen Shot (2).png)\n' +
      'Attached image: /home/u/spike-sandboxes/attachments/t1/b.jpg (photo.jpg)',
  );
});

test('an image alone still sends text, and a name cannot break the line', () => {
  assert.equal(
    withAttachments('', [{ path: shot.path, name: 'two\nlines.png' }]),
    'Attached image: /home/u/spike-sandboxes/attachments/t1/a.png (two lines.png)',
  );
  assert.equal(withAttachments('just text', []), 'just text');
});

test('the history parse recovers the attachments and the text the user typed', () => {
  assert.deepEqual(splitAttachments(withAttachments('look at these\nplease', [shot, photo])), {
    text: 'look at these\nplease',
    attachments: [shot, photo],
  });
  assert.deepEqual(splitAttachments(withAttachments('', [photo])), { text: '', attachments: [photo] });
});

test('text without trailing references is left alone', () => {
  const plain = 'Attached image: /a.png (x.png)\nis how the composer writes it';
  assert.deepEqual(splitAttachments(plain), { text: plain, attachments: [] });
  assert.deepEqual(splitAttachments('hello'), { text: 'hello', attachments: [] });
});

test('sizes read like the composer chip', () => {
  assert.equal(formatBytes(512), '512 B');
  assert.equal(formatBytes(2048), '2.0 KB');
  assert.equal(formatBytes(3.5 * 1024 * 1024), '3.5 MB');
});

test('only png, jpeg, gif and webp up to 20 MB are accepted', () => {
  assert.equal(rejection('image/png', 20 * 1024 * 1024), null);
  assert.equal(rejection('image/webp', 1), null);
  assert.equal(rejection('image/svg+xml', 1), 'not a supported image');
  assert.equal(rejection('application/pdf', 1), 'not a supported image');
  assert.equal(rejection('image/jpeg', 20 * 1024 * 1024 + 1), 'larger than 20 MB');
});

test('a thumbnail path rides on the same line and comes back from the history parse', () => {
  const withThumb = { ...shot, thumbnail: '/home/u/spike-sandboxes/attachments/t1/c.webp' };
  const content = withAttachments('see', [withThumb, photo]);
  assert.equal(
    content,
    'see\n\n' +
      'Attached image: /home/u/spike-sandboxes/attachments/t1/a.png (Screen Shot (2).png) thumbnail: /home/u/spike-sandboxes/attachments/t1/c.webp\n' +
      'Attached image: /home/u/spike-sandboxes/attachments/t1/b.jpg (photo.jpg)',
  );
  assert.deepEqual(splitAttachments(content), { text: 'see', attachments: [withThumb, photo] });
  assert.equal(baseName(withThumb.thumbnail), 'c.webp');
});

test('a thumbnail is typed by its first bytes: JPEG or PNG from the backend, otherwise WebP', () => {
  assert.equal(thumbnailType(new Uint8Array([0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50])), 'image/webp');
  assert.equal(thumbnailType(new Uint8Array([0xff, 0xd8, 0xff, 0xe0])), 'image/jpeg');
  assert.equal(thumbnailType(new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a])), 'image/png');
});

test('dropped paths are separated by spaces, a path with a space in double quotes', () => {
  assert.equal(pathsText(['/Users/a/notes.txt']), '/Users/a/notes.txt');
  assert.equal(pathsText(['/Users/a/My Folder', '/tmp/x.md']), '"/Users/a/My Folder" /tmp/x.md');
});

test('paths go in at the caret with a space either side only where needed', () => {
  assert.deepEqual(insertAt('', 0, '/a'), { draft: '/a', caret: 2 });
  assert.deepEqual(insertAt('look at', 7, '/a'), { draft: 'look at /a', caret: 10 });
  assert.deepEqual(insertAt('look at  please', 8, '/a'), { draft: 'look at /a please', caret: 10 });
  assert.deepEqual(insertAt('seeplease', 3, '/a'), { draft: 'see /a please', caret: 7 });
});
