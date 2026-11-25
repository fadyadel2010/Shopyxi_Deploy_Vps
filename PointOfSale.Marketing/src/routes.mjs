export const siteOrigin = 'https://shopyxi.com';
// Deterministic owner-selected default, never negotiated or stored per visitor.
export const rootRedirect = {
  status: 302, location: '/ar/',
  cacheControl: 'no-store, no-cache, max-age=0, must-revalidate, no-transform',
  robots: 'noindex, follow'
};
export const locales = ['en', 'ar'];
export const pages = ['home', 'platform', 'pos', 'storefront', 'affiliate', 'commercehub'];
export const pagePath = (locale, page) => {
  if (!locales.includes(locale) || !pages.includes(page)) throw Error('Unknown marketing route');
  return `/${locale}/${page === 'home' ? '' : page === 'platform' ? 'platform' : `products/${page}`}`;
};
export const routes = locales.flatMap(locale => pages.map(page => ({
  locale, page, path: pagePath(locale, page),
  equivalent: pagePath(locale === 'en' ? 'ar' : 'en', page),
  dir: locale === 'ar' ? 'rtl' : 'ltr'
})));
export const accessDestinations = [
  'https://admin.shopyxi.com',
  'https://commercehub.shopyxi.com',
  'https://affiliate.shopyxi.com'
];
// Owner-approved self-registration destination for the free experience CTA.
// Same tab, ordinary navigation; no form is submitted from the marketing site.
export const registrationDestination = 'https://admin.shopyxi.com/register';
export const csp = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; font-src 'self'; connect-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'";