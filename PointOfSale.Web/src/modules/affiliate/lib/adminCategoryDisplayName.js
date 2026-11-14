import publicUx from '../i18n/publicUx'

// Reuse the existing display-only taxonomy translations. IDs, slugs, search
// parameters and unknown/custom master-data names remain server-owned.
export function adminCategoryDisplayName(category, language) {
    const locale = language?.startsWith('ar') ? 'ar' : 'en'
    return publicUx[locale].categoryNames[category?.slug] || category?.name || ''
}
