import type { ReactNode } from "react";

export type SortDirection = "asc" | "desc";
export type SortValue = string | number | boolean | null | undefined;

export interface SortState<K extends string> {
  key: K;
  direction: SortDirection;
}

export function toggleSort<K extends string>(current: SortState<K> | null, key: K): SortState<K> {
  if (current?.key === key) {
    return { key, direction: current.direction === "asc" ? "desc" : "asc" };
  }
  return { key, direction: "asc" };
}

function compareValues(a: SortValue, b: SortValue): number {
  if (a == null && b == null) return 0;
  if (a == null) return -1;
  if (b == null) return 1;
  if (typeof a === "number" && typeof b === "number") return a - b;
  if (typeof a === "boolean" && typeof b === "boolean") return a === b ? 0 : a ? 1 : -1;
  return String(a).localeCompare(String(b), undefined, { numeric: true, sensitivity: "base" });
}

// Stale sort state (e.g. left over from a table with different columns) is
// simply ignored rather than throwing, since callers key sort state by
// column name across tables that don't all share the same columns.
export function sortRows<T, K extends string>(
  rows: T[],
  sort: SortState<K> | null,
  accessors: Record<K, (row: T) => SortValue>,
): T[] {
  if (!sort) return rows;
  const accessor = accessors[sort.key];
  if (!accessor) return rows;
  const sorted = [...rows].sort((a, b) => compareValues(accessor(a), accessor(b)));
  return sort.direction === "asc" ? sorted : sorted.reverse();
}

export function SortableHeader<K extends string>({
  label,
  columnKey,
  sort,
  onSort,
}: {
  label: ReactNode;
  columnKey: K;
  sort: SortState<K> | null;
  onSort: (key: K) => void;
}) {
  const active = sort?.key === columnKey;
  return (
    <th
      className="data-table-th-sortable"
      onClick={() => onSort(columnKey)}
      aria-sort={active ? (sort!.direction === "asc" ? "ascending" : "descending") : "none"}
    >
      {label}
      <span className={`sort-arrow${active ? " sort-arrow-active" : ""}`}>
        {active ? (sort!.direction === "asc" ? "▲" : "▼") : "↕"}
      </span>
    </th>
  );
}
