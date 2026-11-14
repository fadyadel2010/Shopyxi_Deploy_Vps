import { useEffect, useRef, useState } from "react";
import { SlidersHorizontal, X } from "lucide-react";

import type { CommerceHubSearchFacets } from "@/types";
import { Button } from "@/ui/button";
import { useCopy } from "@/i18n/useCopy";
import type { DiscoveryAttributeFilter, DiscoveryFiltersState } from "../types";
import { FiltersSidebar } from "./FiltersSidebar";

type MobileFiltersDrawerProps = {
  open: boolean;
  facets: CommerceHubSearchFacets;
  filters: DiscoveryFiltersState;
  onClose: () => void;
  onApply: (filters: DiscoveryFiltersState) => void;
};

const cloneFilters = (filters: DiscoveryFiltersState): DiscoveryFiltersState => ({
  ...filters,
  attributes: Object.fromEntries(
    Object.entries(filters.attributes).map(([key, value]) => [
      key,
      value.type === "terms"
        ? { type: "terms", values: [...value.values] }
        : { type: "range", min: value.min, max: value.max }
    ])
  ) as Record<string, DiscoveryAttributeFilter>
});

export const MobileFiltersDrawer = ({
  open,
  facets,
  filters,
  onClose,
  onApply
}: MobileFiltersDrawerProps) => {
  const [draftFilters, setDraftFilters] = useState<DiscoveryFiltersState>(() => cloneFilters(filters));
  const dialogRef = useRef<HTMLDialogElement>(null);
  const t = useCopy();

  useEffect(() => {
    if (!open) return;
    const opener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const dialog = dialogRef.current;
    dialog?.showModal();
    const oldOverflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => {
      dialog?.close();
      document.body.style.overflow = oldOverflow;
      opener?.focus();
    };
  }, [open]);

  useEffect(() => {
    if (open) {
      setDraftFilters(cloneFilters(filters));
    }
  }, [filters, open]);

  if (!open) {
    return null;
  }

  return (
    <dialog ref={dialogRef} aria-modal="true" aria-labelledby="mobile-filters-title" onCancel={(event) => { event.preventDefault(); onClose(); }} onKeyDown={(event) => {
      if (event.key !== 'Tab') return;
      const controls = Array.from(event.currentTarget.querySelectorAll<HTMLElement>('button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled), a[href], [tabindex="0"]'))
        .filter(element => element.getClientRects().length > 0);
      const first = controls[0];
      const last = controls[controls.length - 1];
      if (!first || !last) { event.preventDefault(); return; }
      if (event.shiftKey && (document.activeElement === first || document.activeElement === event.currentTarget)) {
        event.preventDefault(); last.focus();
      } else if (!event.shiftKey && (document.activeElement === last || document.activeElement === event.currentTarget)) {
        event.preventDefault(); first.focus();
      }
    }} className="fixed inset-0 z-[60] m-0 h-[100dvh] max-h-none w-full max-w-none border-0 bg-background p-0 backdrop:bg-slate-950/45 lg:hidden">
      <div className="h-full bg-background">
        <div className="flex items-center justify-between border-b border-border/70 px-5 py-4">
          <div className="flex items-center gap-2">
            <SlidersHorizontal className="h-4 w-4 text-primary" />
            <h2 id="mobile-filters-title" className="text-base font-semibold text-foreground">{t("Filters")}</h2>
          </div>
          <Button type="button" variant="ghost" size="icon" onClick={onClose} aria-label={t("Close filters")}>
            <X className="h-4 w-4" />
          </Button>
        </div>

        <div className="h-[calc(100vh-8rem)] overflow-y-auto px-5 py-5">
          <FiltersSidebar
            facets={facets}
            filters={draftFilters}
            onCategoryChange={(categoryId) => setDraftFilters((current) => ({ ...current, categoryId }))}
            onInStockChange={(inStock) => setDraftFilters((current) => ({ ...current, inStock }))}
            onPriceChange={(range) =>
              setDraftFilters((current) => ({
                ...current,
                priceMin: range.min,
                priceMax: range.max
              }))
            }
            onAttributeTermsChange={(key, values) =>
              setDraftFilters((current) => ({
                ...current,
                attributes: values.length > 0
                  ? {
                      ...current.attributes,
                      [key]: {
                        type: "terms",
                        values
                      }
                    }
                  : Object.fromEntries(
                      Object.entries(current.attributes).filter(([attributeKey]) => attributeKey !== key)
                    )
              }))
            }
            onAttributeRangeChange={(key, range) =>
              setDraftFilters((current) => {
                const nextAttributes = { ...current.attributes };
                if (range.min === undefined && range.max === undefined) {
                  delete nextAttributes[key];
                } else {
                  nextAttributes[key] = {
                    type: "range",
                    min: range.min,
                    max: range.max
                  };
                }

                return {
                  ...current,
                  attributes: nextAttributes
                };
              })
            }
            onClear={() =>
              setDraftFilters((current) => ({
                ...current,
                categoryId: "",
                inStock: false,
                priceMin: undefined,
                priceMax: undefined,
                attributes: {}
              }))
            }
          />
        </div>

        <div className="grid grid-cols-2 gap-3 border-t border-border/70 px-5 py-4">
          <Button
            type="button"
            variant="outline"
            onClick={() =>
              setDraftFilters((current) => ({
                ...current,
                categoryId: "",
                inStock: false,
                priceMin: undefined,
                priceMax: undefined,
                attributes: {}
              }))
            }
          >
            {t("Clear all")}
          </Button>
          <Button
            type="button"
            onClick={() => {
              onApply(draftFilters);
              onClose();
            }}
          >
            {t("Apply filters")}
          </Button>
        </div>
      </div>
    </dialog>
  );
};
