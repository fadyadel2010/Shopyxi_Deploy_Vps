/** Display only: never change a category's identity, slug, saved URL, or DB name. */
export function categoryDisplayName(category, t) {
    const key = `public.categoryNames.${category?.slug}`
    const translated = t(key)
    return typeof translated === 'string' && translated.trim() && translated !== key
        ? translated
        : (category?.name?.trim() || t('public.category'))
}
