import { useQueries, useQuery } from "@tanstack/react-query";
import { useMemo } from "react";
import { useNavigate } from "react-router-dom";

import { categoryTreeQueryOptions } from "@/features/discovery/queries";
import {
  extractHomepageProductIds,
  getHomepageAutomaticProductFetchSize,
  resolveHomepageSections
} from "@/homepage/resolveHomepageSections";
import { homepageSectionsQueryOptions } from "@/homepage/queries";
import { SectionRenderer } from "@/homepage/SectionRenderer";
import { productDetailQueryOptions, productsPageQueryOptions } from "@/products";
import { EmptyState } from "@/shared/EmptyState";
import { LoadingSkeleton } from "@/shared/LoadingSkeleton";
import { useDocumentTitle } from "@/utils";
import { useCopy } from "@/i18n/useCopy";

export const HomePage = () => {
  const navigate = useNavigate();
  const t = useCopy();
  const sectionsQuery = useQuery(homepageSectionsQueryOptions());
  const categoriesQuery = useQuery(categoryTreeQueryOptions());
  const productIds = useMemo(() => extractHomepageProductIds(sectionsQuery.data ?? []), [sectionsQuery.data]);
  const automaticProductsPageSize = useMemo(
    () => getHomepageAutomaticProductFetchSize(sectionsQuery.data ?? []),
    [sectionsQuery.data]
  );
  const automaticProductsQuery = useQuery({
    ...productsPageQueryOptions({}, 1, automaticProductsPageSize || 1),
    enabled: automaticProductsPageSize > 0,
    staleTime: 5 * 60 * 1000
  });
  const productQueries = useQueries({
    queries: productIds.map((id) => ({
      ...productDetailQueryOptions(id),
      staleTime: 5 * 60 * 1000
    }))
  });

  useDocumentTitle("Discover");

  const productStatesById = useMemo(
    () =>
      new Map(
        productIds.map((id, index) => [
          id,
          {
            data: productQueries[index]?.data,
            isPending: productQueries[index]?.isPending ?? false,
            isError: productQueries[index]?.isError ?? false,
            errorMessage: productQueries[index]?.isError ? productQueries[index].error.message : undefined
          }
        ])
      ),
    [productIds, productQueries]
  );

  const sections = useMemo(
    () =>
      resolveHomepageSections({
        rawSections: sectionsQuery.data ?? [],
        categories: categoriesQuery.data ?? [],
        categoriesStatus: categoriesQuery.isPending
          ? "loading"
          : categoriesQuery.isError
            ? "error"
            : (categoriesQuery.data?.length ?? 0) > 0
              ? "ready"
              : "empty",
        categoriesErrorMessage: categoriesQuery.isError ? categoriesQuery.error.message : undefined,
        productStatesById,
        automaticProducts: automaticProductsQuery.data?.items ?? [],
        automaticProductsStatus:
          automaticProductsPageSize === 0
            ? "empty"
            : automaticProductsQuery.isPending
              ? "loading"
              : automaticProductsQuery.isError
                ? "error"
                : (automaticProductsQuery.data?.items.length ?? 0) > 0
                  ? "ready"
                  : "empty",
        automaticProductsErrorMessage: automaticProductsQuery.isError
          ? automaticProductsQuery.error.message
          : undefined
      }),
    [
      automaticProductsPageSize,
      automaticProductsQuery.data,
      automaticProductsQuery.error,
      automaticProductsQuery.isError,
      automaticProductsQuery.isPending,
      categoriesQuery.data,
      categoriesQuery.error,
      categoriesQuery.isError,
      categoriesQuery.isPending,
      productStatesById,
      sectionsQuery.data
    ]
  );

  if (sectionsQuery.isPending) {
    return (
      <div className="space-y-6">
        <LoadingSkeleton className="min-h-[360px]" lines={4} />
        <LoadingSkeleton className="min-h-[220px]" lines={3} />
        <LoadingSkeleton className="min-h-[220px]" lines={3} />
      </div>
    );
  }

  if (sectionsQuery.isError) {
    return (
      <EmptyState
        title={t("Homepage unavailable")}
        description={sectionsQuery.error.message || "We couldn't load the Shopixy homepage sections right now."}
      />
    );
  }

  if (sections.length === 0) {
    return (
      <EmptyState
        title={t("Welcome to Shopixy")}
        description={t("Explore products from our marketplace merchants.")}
        actionLabel={t("Browse products")}
        onAction={() => navigate('/products')}
      />
    );
  }

  return <SectionRenderer sections={sections} />;
};
