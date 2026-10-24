/**
 * The tab strip's class recipe, lifted out of `tabs-list.svelte` unchanged.
 *
 * Same reason as the button's: this project checks types with plain `tsc`,
 * which cannot read a `.svelte` file, so anything a `.ts` file imports by name
 * has to live in a `.ts` file. The recipe below is verbatim from the registry.
 */
import { tv, type VariantProps } from "tailwind-variants";

export const tabsListVariants = tv({
	base: "rounded-lg p-[3px] group-data-[orientation=horizontal]/tabs:h-8 data-[variant=line]:rounded-none group/tabs-list text-muted-foreground inline-flex w-fit items-center justify-center group-data-[orientation=vertical]/tabs:h-fit group-data-[orientation=vertical]/tabs:flex-col",
	variants: {
		variant: {
			default: "cn-tabs-list-variant-default bg-muted",
			line: "cn-tabs-list-variant-line gap-1 bg-transparent",
		},
	},
	defaultVariants: {
		variant: "default",
	},
});

export type TabsListVariant = VariantProps<typeof tabsListVariants>["variant"];
