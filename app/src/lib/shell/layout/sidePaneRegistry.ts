/**
 * The typed roster for primary left/right/bottom panes.
 *
 * This module is deliberately boring: it validates a roster and turns a
 * registration into the DOM-facing `PaneSpec` consumed by paneStack.ts. It
 * does not mount a component, keep state, read storage, or call a service.
 */
import type { Component } from 'svelte';

import type { PaneSpec } from './paneStack.ts';

export type SidePaneRegion = 'left' | 'right' | 'bottom';

export const LEFT_SIDE_PANE_IDS = ['working', 'done', 'settled'] as const;

export interface SidePaneRegistration {
  id: string;
  title: string;
  region: SidePaneRegion;
  component: Component;
  minimumSize: number;
  preferredSize: number;
  maximumSize: number | null;
  defaultExpanded: boolean;
  persistent: boolean;
  order: number;
}

export interface SidePaneValidationResult {
  valid: boolean;
  errors: string[];
}

const REGIONS: readonly SidePaneRegion[] = ['left', 'right', 'bottom'];

function isRegion(value: unknown): value is SidePaneRegion {
  return typeof value === 'string' && (REGIONS as readonly string[]).includes(value);
}

function isFiniteNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}

/**
 * Validate all roster invariants without throwing. IDs and order numbers are
 * unique inside the roster/region respectively; size and persistence fields
 * are checked before a caller asks paneStack to consume the roster.
 */
export function validateSidePaneRegistrations(
  registrations: readonly SidePaneRegistration[]
): SidePaneValidationResult {
  const errors: string[] = [];
  if (!Array.isArray(registrations)) {
    return { valid: false, errors: ['registrations must be an array'] };
  }

  const ids = new Set<string>();
  const orders = new Map<SidePaneRegion, Set<number>>();

  registrations.forEach((registration, index) => {
    const label = `registration[${index}]`;
    if (!registration || typeof registration !== 'object') {
      errors.push(`${label} must be an object`);
      return;
    }

    const id = typeof registration.id === 'string' ? registration.id.trim() : '';
    if (!id) errors.push(`${label}.id must be a non-empty string`);
    else if (ids.has(id)) errors.push(`duplicate pane id: ${id}`);
    else ids.add(id);

    if (typeof registration.title !== 'string' || registration.title.trim().length === 0) {
      errors.push(`${label}.title must be a non-empty string`);
    }
    if (!isRegion(registration.region)) {
      errors.push(`${label}.region must be left, right, or bottom`);
    } else {
      const regionOrders = orders.get(registration.region) ?? new Set<number>();
      orders.set(registration.region, regionOrders);
      if (!Number.isInteger(registration.order) || registration.order < 0) {
        errors.push(`${label}.order must be a non-negative integer`);
      } else if (regionOrders.has(registration.order)) {
        errors.push(`duplicate ${registration.region} pane order: ${registration.order}`);
      } else {
        regionOrders.add(registration.order);
      }
    }

    if (registration.component === null || registration.component === undefined) {
      errors.push(`${label}.component is required`);
    }
    if (!isFiniteNumber(registration.minimumSize) || registration.minimumSize < 0) {
      errors.push(`${label}.minimumSize must be a finite number >= 0`);
    }
    if (!isFiniteNumber(registration.preferredSize) || registration.preferredSize <= 0) {
      errors.push(`${label}.preferredSize must be a finite number > 0`);
    }
    if (
      registration.maximumSize !== null &&
      (!isFiniteNumber(registration.maximumSize) || registration.maximumSize <= 0)
    ) {
      errors.push(`${label}.maximumSize must be null or a finite number > 0`);
    }
    if (
      isFiniteNumber(registration.minimumSize) &&
      isFiniteNumber(registration.preferredSize) &&
      registration.minimumSize > registration.preferredSize
    ) {
      errors.push(`${label}.minimumSize cannot exceed preferredSize`);
    }
    if (
      registration.maximumSize !== null &&
      isFiniteNumber(registration.maximumSize) &&
      isFiniteNumber(registration.preferredSize) &&
      registration.maximumSize < registration.preferredSize
    ) {
      errors.push(`${label}.maximumSize cannot be below preferredSize`);
    }
    if (typeof registration.defaultExpanded !== 'boolean') {
      errors.push(`${label}.defaultExpanded must be boolean`);
    }
    if (typeof registration.persistent !== 'boolean') {
      errors.push(`${label}.persistent must be boolean`);
    }
  });

  return { valid: errors.length === 0, errors };
}

/** Throw a useful boundary error before an invalid roster reaches dockview. */
export function assertValidSidePaneRegistrations(
  registrations: readonly SidePaneRegistration[]
): asserts registrations is readonly SidePaneRegistration[] {
  const result = validateSidePaneRegistrations(registrations);
  if (!result.valid) throw new TypeError(`Invalid side-pane registry: ${result.errors.join('; ')}`);
}

export function isValidSidePaneRegistrations(
  registrations: readonly SidePaneRegistration[]
): boolean {
  return validateSidePaneRegistrations(registrations).valid;
}

/** Registrations for one region, in persisted/default order. */
export function registrationsForRegion(
  registrations: readonly SidePaneRegistration[],
  region: SidePaneRegion
): SidePaneRegistration[] {
  assertValidSidePaneRegistrations(registrations);
  return registrations
    .filter((registration) => registration.region === region)
    .sort((a, b) => a.order - b.order || a.id.localeCompare(b.id));
}

export type PaneElementSource =
  | ReadonlyMap<string, HTMLElement>
  | Readonly<Record<string, HTMLElement>>
  | ((registration: SidePaneRegistration) => HTMLElement | null | undefined);

function elementFor(
  source: PaneElementSource,
  registration: SidePaneRegistration
): HTMLElement | null | undefined {
  if (typeof source === 'function') return source(registration);
  const mapLike = source as ReadonlyMap<string, HTMLElement>;
  if (typeof mapLike.get === 'function') return mapLike.get(registration.id);
  return (source as Readonly<Record<string, HTMLElement>>)[registration.id];
}

/** Map one validated registration to the existing PaneStack input shape. */
export function sidePaneRegistrationToPaneSpec(
  registration: SidePaneRegistration,
  element: HTMLElement
): PaneSpec {
  assertValidSidePaneRegistrations([registration]);
  if (!element || typeof element !== 'object') {
    throw new TypeError(`Missing DOM element for side pane: ${registration.id}`);
  }
  return {
    id: registration.id.trim(),
    title: registration.title.trim(),
    element,
    size: registration.preferredSize,
    expanded: registration.defaultExpanded,
    minimumSize: registration.minimumSize,
    maximumSize: registration.maximumSize,
    persistent: registration.persistent
  };
}

/**
 * Map a roster to PaneSpecs without mounting anything. The caller supplies the
 * already-authored parking elements; missing elements are a boundary error.
 */
export function sidePaneRegistrationsToPaneSpecs(
  registrations: readonly SidePaneRegistration[],
  elements: PaneElementSource,
  region?: SidePaneRegion
): PaneSpec[] {
  assertValidSidePaneRegistrations(registrations);
  const selected = region ? registrationsForRegion(registrations, region) : [...registrations];
  return selected.map((registration) => {
    const element = elementFor(elements, registration);
    if (!element) throw new TypeError(`Missing DOM element for side pane: ${registration.id}`);
    return sidePaneRegistrationToPaneSpec(registration, element);
  });
}

// Short aliases keep the registry pleasant to consume while retaining one
// implementation and one validation path.
export const toPaneSpecs = sidePaneRegistrationsToPaneSpecs;
export const mapSidePaneRegistration = sidePaneRegistrationToPaneSpec;
export const validateSidePaneRegistry = validateSidePaneRegistrations;
export const mapSidePaneRegistrationsToPaneSpecs = sidePaneRegistrationsToPaneSpecs;
