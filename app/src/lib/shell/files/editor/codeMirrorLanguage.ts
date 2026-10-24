import type { Extension } from '@codemirror/state';
import { StreamLanguage } from '@codemirror/language';

export function enhancedCSharpTokenStyle(
  style: string | null,
  token: string,
  before: string,
  after: string
): string | null {
  if (style !== 'variable') return style;

  const prefix = before.trimEnd();
  const called = /^\s*\(/.test(after);
  if (prefix.endsWith('.')) return called ? 'def' : 'property';
  if (/\bnew\s*$/.test(prefix)) return 'type';
  if (called) return 'def';
  return /^[A-Z]/.test(token) ? 'type' : style;
}

export async function loadCodeMirrorLanguage(language: string): Promise<Extension> {
  switch (language) {
    case 'javascript':
    case 'jsx': {
      const { javascript } = await import('@codemirror/lang-javascript');
      return javascript({ jsx: language === 'jsx' });
    }
    case 'typescript':
    case 'tsx': {
      const { javascript } = await import('@codemirror/lang-javascript');
      return javascript({ typescript: true, jsx: language === 'tsx' });
    }
    case 'json': {
      const { json } = await import('@codemirror/lang-json');
      return json();
    }
    case 'css': {
      const { css } = await import('@codemirror/lang-css');
      return css();
    }
    case 'html':
    case 'svelte':
    case 'vue':
    case 'razor':
    case 'php': {
      const { html } = await import('@codemirror/lang-html');
      return html({ matchClosingTags: true, autoCloseTags: true });
    }
    case 'markdown':
    case 'mdx': {
      const { markdown } = await import('@codemirror/lang-markdown');
      return markdown();
    }
    case 'python': {
      const { python } = await import('@codemirror/lang-python');
      return python();
    }
    case 'rust': {
      const { rust } = await import('@codemirror/lang-rust');
      return rust();
    }
    case 'sql': {
      const { sql } = await import('@codemirror/lang-sql');
      return sql();
    }
    case 'xml': {
      const { xml } = await import('@codemirror/lang-xml');
      return xml();
    }
    case 'yaml': {
      const { yaml } = await import('@codemirror/lang-yaml');
      return yaml();
    }
    case 'java': {
      const { java } = await import('@codemirror/lang-java');
      return java();
    }
    case 'cpp': {
      const { cpp } = await import('@codemirror/lang-cpp');
      return cpp();
    }
    case 'csharp': {
      const { csharp } = await import('@codemirror/legacy-modes/mode/clike');
      return StreamLanguage.define({
        ...csharp,
        token(stream, state) {
          const style = csharp.token(stream, state);
          return enhancedCSharpTokenStyle(
            style,
            stream.current(),
            stream.string.slice(0, stream.start),
            stream.string.slice(stream.pos)
          );
        }
      });
    }
    case 'kotlin': {
      const { kotlin } = await import('@codemirror/legacy-modes/mode/clike');
      return StreamLanguage.define(kotlin);
    }
    case 'dart': {
      const { dart } = await import('@codemirror/legacy-modes/mode/clike');
      return StreamLanguage.define(dart);
    }
    case 'fsharp': {
      const { fSharp } = await import('@codemirror/legacy-modes/mode/mllike');
      return StreamLanguage.define(fSharp);
    }
    case 'go': {
      const { go } = await import('@codemirror/legacy-modes/mode/go');
      return StreamLanguage.define(go);
    }
    case 'shell': {
      const { shell } = await import('@codemirror/legacy-modes/mode/shell');
      return StreamLanguage.define(shell);
    }
    case 'powershell': {
      const { powerShell } = await import('@codemirror/legacy-modes/mode/powershell');
      return StreamLanguage.define(powerShell);
    }
    case 'ruby': {
      const { ruby } = await import('@codemirror/legacy-modes/mode/ruby');
      return StreamLanguage.define(ruby);
    }
    case 'lua': {
      const { lua } = await import('@codemirror/legacy-modes/mode/lua');
      return StreamLanguage.define(lua);
    }
    case 'swift': {
      const { swift } = await import('@codemirror/legacy-modes/mode/swift');
      return StreamLanguage.define(swift);
    }
    case 'toml': {
      const { toml } = await import('@codemirror/legacy-modes/mode/toml');
      return StreamLanguage.define(toml);
    }
    case 'ini': {
      const { properties } = await import('@codemirror/legacy-modes/mode/properties');
      return StreamLanguage.define(properties);
    }
    case 'scss':
    case 'less': {
      const modes = await import('@codemirror/legacy-modes/mode/css');
      return StreamLanguage.define(language === 'less' ? modes.less : modes.sCSS);
    }
    case 'dockerfile': {
      const { dockerFile } = await import('@codemirror/legacy-modes/mode/dockerfile');
      return StreamLanguage.define(dockerFile);
    }
    case 'protobuf': {
      const { protobuf } = await import('@codemirror/legacy-modes/mode/protobuf');
      return StreamLanguage.define(protobuf);
    }
    default:
      return [];
  }
}
