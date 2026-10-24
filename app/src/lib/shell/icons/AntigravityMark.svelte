<!--
  AntigravityMark.svelte — Google's own Antigravity symbol.

  Google publishes no vector for this icon. The URL that ends in `.svg` on
  their site is a 1600px PNG wrapped in an <image> tag (a 900KB file, no path
  data in it), and the press page at https://antigravity.google/press offers
  the icon only as PNG. So the real mark gets in as a mask rather than a path:
  the shape below is the alpha channel of Google's own one-colour icon,
  `antigravity-icon__one-color.png` from that press page, trimmed to the mark
  and scaled to icon size. Nothing about the shape was redrawn.

  Because it is a mask, `currentColor` paints it and it follows the theme in
  the same way the two path-based marks do.
-->
<script lang="ts">
  interface Props {
    class?: string;
    'aria-hidden'?: boolean | 'true' | 'false';
    'aria-label'?: string;
  }
  let { class: className, ...rest }: Props = $props();

  const MASK = 'url("data:image/png;base64,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")';
</script>

<span {...rest} class={className} style="--antigravity-mask: {MASK}" role="presentation"></span>

<style>
  span {
    display: inline-block;
    background-color: currentColor;
    mask-image: var(--antigravity-mask);
    mask-repeat: no-repeat;
    mask-position: center;
    mask-size: contain;
  }
</style>
