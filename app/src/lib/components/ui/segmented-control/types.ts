/**
 * The segmented control's item shape, in a `.ts` file for the same reason the
 * button's class recipe is: this project checks types with plain `tsc`, which
 * cannot read a `.svelte` file, so a name another module imports has to live
 * outside the component.
 */
import type { Component } from "svelte";

export type SegmentedControlItem = {
	value: string;
	label: string;
	/** Optional leading icon; a lucide component, or anything shaped like one. */
	icon?: Component<{ class?: string; "aria-hidden"?: "true" }>;
};
