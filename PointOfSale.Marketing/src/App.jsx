import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { MarketingContext, Claim, Label, Mixed, Actions, claims, labels, products, useMarketing } from './components/core.jsx';
import { Header, Footer, Demo } from './components/layout.jsx';
import { Introductions, Relationships, Capabilities, FAQ, Reservation, Relief } from './components/sections.jsx';
import { HomeHero } from './components/homepage.jsx';
import { ProductShowcase } from './components/product-showcase.jsx';
import { EcosystemRelationships } from './components/ecosystem-relationships.jsx';
import { BusinessCapabilitiesShowcase } from './components/business-capabilities-showcase.jsx';
import { CapabilitiesCoverageSection } from './components/capabilities-coverage.jsx';
import { FreeExperienceCTA } from './components/free-experience-cta.jsx';
import { PlatformEcosystemComposition, ProductShowcaseComposition } from './components/composition.jsx';
import { PageLink } from './components/core.jsx';
import { metadata } from './metadata.mjs';
import { csp, pagePath, siteOrigin } from './routes.mjs';
import { capabilityGroups } from './components/editorial.jsx';
import { ProductScreenshot, ScreenshotDisclosure } from './components/product-screen.jsx';

// Phase 1F-A: legacy Proof, Trust and FAQ are detached from the homepage
// composition only. Their components/exports, claims and labels stay intact for
// reuse on other routes and for the later Coverage & Limits phase.
// Phase 1F-D: the review-only Demo placeholder is removed from the homepage in
// both rendering modes; the permanent Free Experience CTA takes its place. Demo
// itself is untouched and still renders on Platform and product routes.
function Home() {
  return <>
    <HomeHero />
    <ProductShowcase />
    <EcosystemRelationships /><BusinessCapabilitiesShowcase />
    <CapabilitiesCoverageSection />
    <FreeExperienceCTA />
  </>;
}
function Platform() {
  const { t } = useMarketing();
  return <>
    <section className="platform-hero"><div className="wrap"><Label id="platform" className="eyebrow" />
      <div className="platform-heading"><Claim id="C43" as="h1" /><Claim id="C01" /></div>
      <PlatformEcosystemComposition /><Actions />
    </div></section>
    <Relationships /><Capabilities />
    <section className="section wrap rule" id="integrations"><div className="section-head"><h2>{t('caveats')}</h2><div><Claim id="C27" /><Claim id="C37" /></div></div></section>
    <Introductions /><FAQ /><Demo />
  </>;
}
function ProductScope({ p }) {
  const { t, locale } = useMarketing();
  if (p.key === 'storefront') return <div className="audience-spread">
    <article><h3>{t('operational')}</h3><Claim id="C20" /><Claim id="C21" /></article>
    <article><h3>{t('shopping')}</h3><Claim id="C18" /><Claim id="C19" /></article>
  </div>;
  if (p.key === 'affiliate') return <div className="lifecycle"><Claim id="C23" as="h3" />
    <ul className="concept-register">{(locale === 'ar'
      ? ['النقرات', 'الطلبات المنسوبة للإحالات', 'سجل العمولات', 'الاعتماد', 'الدفع الفعلي']
      : ['Clicks', 'Attributed orders', 'Commission history', 'Approval', 'Actual payment']).map(text => <li key={text}>{text}</li>)}</ul>
    <Claim id="C38" /></div>;
  if (p.key === 'commercehub') return <div className="publishing-spread">
    <article><h3>{t('selling')}</h3><Claim id="C32" /><Claim id="C34" /></article>
    <article><h3>{t('shopping')}</h3><Claim id="C05" /><Claim id="C35" /></article></div>;
   return <div className="warehouse-spread"><ProductScreenshot product="pos" compact /><div><Claim id="C09" as="h3" /><Claim id="C15" /></div></div>;
}
function Product({ p }) {
  const { t, locale } = useMarketing();
  return <>
    <section className={`product-hero product-${p.key}`}><div className="wrap">
      <div className="product-opening"><div><div className="product-mast"><bdi className="product-number" dir="ltr" aria-hidden="true">{p.n}</bdi>
        <div><span className="eyebrow">{t('template')} / <Mixed text={t(p.key)} /></span><Claim id={p.headline} as="h1" /></div>
      </div><Claim id={p.desc} /><Actions /></div><ProductShowcaseComposition product={p.key} priority /></div>
    </div></section>
    <section className="section product-scope"><div className="wrap"><h2 className="eyebrow">{t('productdetail')}</h2><ProductScope p={p} /></div></section>
    <section className="section wrap"><div className="product-detail"><div><h2>{t('capabilities')}</h2>
      <ul className={`capability-cards cards-${p.key}`}>{p.caps.map((id, i) => <li key={id}>
        <span className="eyebrow"><bdi dir="ltr">0{i + 1}</bdi></span>
        <h3>{capabilityGroups[locale][p.key][i]}</h3><Claim id={id} as="div" /></li>)}</ul>
    </div><aside className="product-boundaries"><h2>{t('caveats')}</h2>{p.boundary.map(id => <Claim key={id} id={id} />)}</aside></div></section>
    <section className="section wrap related" id="products" tabIndex="-1"><h2>{t('related')}</h2>
      <div className="related-list">{products.filter(x => x.key !== p.key).map(x => <article key={x.key}>
        <ProductScreenshot product={x.key} compact disclosure={false} />
        <bdi className="index" dir="ltr">{x.n}</bdi><h3><Mixed text={t(x.key)} /></h3><Claim id={x.desc} />
        <PageLink page={x.key}>{t('productstudy')}</PageLink>
       </article>)}</div><ScreenshotDisclosure desktopNote />
    </section><Demo />
  </>;
}
function Head({ route, assets, publicRelease }) {
  const meta = metadata(route, claims, labels);
  return <head><meta charSet="utf-8" /><meta name="viewport" content="width=device-width, initial-scale=1" />
    <meta httpEquiv="Content-Security-Policy" content={csp} />
    <meta name="robots" content={publicRelease ? 'index, follow' : 'noindex, nofollow, noarchive'} /><meta name="referrer" content="no-referrer" />
    <meta name="shopyxi-release-state" content={publicRelease ? 'PUBLIC_APPROVED_LEAN' : 'INTERNAL_REVIEW_ONLY'} />
    <title>{meta.title}</title><meta name="description" content={meta.description} data-claim={meta.claim} />
    <link rel="canonical" href={`${siteOrigin}${route.path}`} />
    {['en', 'ar'].map(locale => <link key={locale} rel="alternate" hrefLang={locale} href={`${siteOrigin}${pagePath(locale, route.page)}`} />)}
    {route.page === 'home' && <link rel="alternate" hrefLang="x-default" href={`${siteOrigin}/ar/`} />}
    <meta property="og:type" content="website" /><meta property="og:site_name" content="SHOPYXI" />
    <meta property="og:title" content={meta.title} /><meta property="og:description" content={meta.description} data-claim={meta.claim} />
    <meta property="og:url" content={`${siteOrigin}${route.path}`} />
    <meta property="og:locale" content={route.locale === 'ar' ? 'ar_EG' : 'en_US'} />
    <meta property="og:locale:alternate" content={route.locale === 'ar' ? 'en_US' : 'ar_EG'} />
    <meta property="og:image" content={`${siteOrigin}/assets/shopyxi-logo.png`} />
    <meta property="og:image:width" content="2400" /><meta property="og:image:height" content="747" />
    <meta property="og:image:alt" content="SHOPYXI" />
    <link rel="icon" type="image/png" href="/assets/shopyxi-logo.png" />
    <link rel="preload" href="/assets/cairo-latin-variable.woff2" as="font" type="font/woff2" crossOrigin="anonymous" />
    {route.locale === 'ar' && <link rel="preload" href="/assets/cairo-arabic-variable.woff2" as="font" type="font/woff2" crossOrigin="anonymous" />}
    <link rel="stylesheet" href={assets.css} /><script src={assets.js} defer />
  </head>;
}
export function render(route, assets, publicRelease = false) {
  const value = { ...route, publicRelease, t: id => labels[route.locale][id] };
  const p = products.find(p => p.key === route.page);
  return '<!doctype html>' + renderToStaticMarkup(
    <html lang={route.locale} dir={route.dir}><Head route={route} assets={assets} publicRelease={publicRelease} />
      <body data-shopyxi-marketing="" data-review-only={publicRelease ? undefined : 'true'} data-public-release={publicRelease ? 'lean' : undefined}><MarketingContext.Provider value={value}>
        <Header /><main id="main" tabIndex="-1">{route.page === 'home' ? <Home /> : route.page === 'platform' ? <Platform /> : <Product p={p} />}</main><Footer />
      </MarketingContext.Provider></body>
    </html>);
}
export function renderRoot(assets, publicRelease = false) {
  // Defensive fallback for file-only hosts. Actual HTTP servers return 302 before
  // serving a document; this is not an indexable language-selection landing page.
  return '<!doctype html>' + renderToStaticMarkup(<html lang="ar" dir="rtl"><head>
    <meta charSet="utf-8" /><meta name="viewport" content="width=device-width, initial-scale=1" />
    <meta name="robots" content="noindex, follow" />
    <meta httpEquiv="refresh" content="0;url=/ar/" />
    <link rel="canonical" href={`${siteOrigin}/ar/`} /><title>SHOPYXI</title>
  </head><body><main><a href="/ar/">العربية</a></main></body></html>);
}