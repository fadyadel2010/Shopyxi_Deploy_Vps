import { ArrowDownUp } from "lucide-react";

import { PRODUCT_SORT_OPTIONS } from "@/features/discovery/constants";
import type { CommerceHubProductSort } from "@/types";
import { useCopy } from "@/i18n/useCopy";

type ProductSortSelectProps = {
  value: CommerceHubProductSort | "";
  onChange: (value: CommerceHubProductSort | "") => void;
};

export const ProductSortSelect = ({ value, onChange }: ProductSortSelectProps) => {
  const t = useCopy();
  const arabicLabels: Record<string, string> = { "Relevance": "الأكثر صلة", "Price Low to High": "السعر: من الأقل للأعلى", "Price High to Low": "السعر: من الأعلى للأقل", "Newest Products": "أحدث المنتجات" };
  return (
  <label className="flex items-center gap-3 rounded-full border border-border/80 bg-white px-4 py-2.5 text-sm text-foreground shadow-sm">
    <ArrowDownUp className="h-4 w-4 text-muted-foreground" />
    <span className="font-medium text-muted-foreground">{t("Sort", "ترتيب")}</span>
    <select
      className="min-w-[12rem] bg-transparent text-sm font-semibold text-foreground focus:outline-none"
      value={value}
      onChange={(event) => onChange(event.target.value as CommerceHubProductSort | "")}
      aria-label={t("Sort products", "ترتيب المنتجات")}
    >
      <option value="">{t("Recommended", "موصى به")}</option>
      {PRODUCT_SORT_OPTIONS.map((option) => (
        <option key={option.value} value={option.value}>
          {t(option.label, arabicLabels[option.label])}
        </option>
      ))}
    </select>
  </label>
  );
};
