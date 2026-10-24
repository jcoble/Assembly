import { clsx, type ClassValue } from 'clsx';
import { twMerge } from 'tailwind-merge';

/**
 * Join class names, letting the last one win.
 *
 * Every shadcn component takes an optional `class` prop so a caller can
 * adjust it. Plain string concatenation would leave both the component's
 * class and the caller's in the markup, and which one actually paints
 * would come down to the order Tailwind happened to write them in.
 * `cn` resolves that: it drops any class the caller's class replaces
 * (pass `p-6` and the component's own `p-4` disappears) and accepts the
 * usual conditional shapes — strings, arrays, `{ 'is-open': open }`.
 */
export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}

/* ------------------------------------------------------------------
 * Prop-shape helpers the shadcn components import.
 *
 * They exist because a component that renders its own markup must not
 * also advertise the escape hatches that would replace that markup.
 * Nothing in the shell needs to use these directly.
 * ------------------------------------------------------------------ */

/** The same props, minus the `child` snippet that would replace the element. */
export type WithoutChild<T> = T extends { child?: unknown } ? Omit<T, 'child'> : T;

/** The same props, minus the `children` snippet. */
export type WithoutChildren<T> = T extends { children?: unknown } ? Omit<T, 'children'> : T;

/** The same props, minus both snippets. */
export type WithoutChildrenOrChild<T> = WithoutChildren<WithoutChild<T>>;

/** The same props, plus a `ref` the caller can bind to reach the real element. */
export type WithElementRef<T, U extends HTMLElement = HTMLElement> = T & { ref?: U | null };
