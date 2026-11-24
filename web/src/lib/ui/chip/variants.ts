/**
 * The chip's class recipe, in a `.ts` file for the same reason the button's and
 * the badge's are: this project checks types with plain `tsc`, which cannot
 * read a `.svelte` file, so anything a `.ts` file imports by name has to live
 * here.
 *
 * A chip is the small pill a panel puts beside a title or on a row: how many
 * things are in a group, whether something needs you, whether it is running,
 * finished, or broken. `tone` names what the chip MEANS rather than a color —
 * the four status tones read the shell's own status tokens, which is the same
 * exception `hover-action-button.svelte` makes and for the same reason: the
 * component registry has no slot for "needs you" or "running".
 */
import { type VariantProps, tv } from "tailwind-variants";

export const chipVariants = tv({
	base: "inline-flex h-5 w-fit shrink-0 items-center justify-center gap-1 rounded-full px-2 text-sm leading-none font-medium whitespace-nowrap [&>svg]:size-3 [&>svg]:shrink-0",
	variants: {
		tone: {
			neutral: "bg-secondary text-secondary-foreground",
			count: "bg-muted text-muted-foreground tabular-nums",
			attention: "bg-[var(--color-attention-bg)] text-[var(--color-attention)]",
			live: "bg-[var(--color-live-bg)] text-[var(--color-live)]",
			good: "bg-[var(--color-good-bg)] text-[var(--color-good)]",
			bad: "bg-[var(--color-bad-bg)] text-[var(--color-bad)]",
		},
	},
	defaultVariants: {
		tone: "neutral",
	},
});

export type ChipTone = NonNullable<VariantProps<typeof chipVariants>["tone"]>;
