import React from 'react';
import { Claim, Label, Mixed, PageLink, products, useMarketing } from './core.jsx';
import { ProductScreenshot, ScreenshotDisclosure } from './product-screen.jsx';

const shapes = {
  pos: ['M42 70 130 30l88 40-88 40z', 'm42 100 88 40 88-40M42 130l88 40 88-40M42 160l88 40 88-40', 'M130 110v90'],
  storefront: ['M62 206V100a68 68 0 0 1 136 0v106M84 206V102a46 46 0 0 1 92 0v104M106 206V104a24 24 0 0 1 48 0v102', 'M40 206h180'],
  affiliate: ['m111 80 20-20a43 43 0 0 1 61 61l-28 28a43 43 0 0 1-57 3M149 160l-20 20a43 43 0 0 1-61-61l28-28a43 43 0 0 1 57-3', 'm99 143 62-62'],
  commercehub: ['M30 90 130 35l100 55M45 100h170M45 205h170M60 110v80M90 110v80M120 110v80M150 110v80M180 110v80M210 110v80']
};
export function Relief({ product }) {
  return <svg className="relief" aria-hidden="true" viewBox="0 0 260 240" fill="none" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round">
    {shapes[product].map(d => <path key={d} d={d} />)}
  </svg>;
}
export function Portfolio() {
  const { t } = useMarketing();
  return <aside className="portfolio" aria-label={t('maplabel')}>
    <div className="portfolio-caption"><span>{t('maplabel')}</span><bdi dir="ltr">01—04</bdi></div>
    <div className="folio-composition">{products.map(p => <div className={`folio folio-${p.key}`} key={p.key}>
      <div className="folio-top"><bdi dir="ltr">{p.n}</bdi></div><ProductScreenshot product={p.key} priority folio disclosure={false} />
      <div className="folio-label"><strong><Mixed text={t(p.key)} /></strong>
        <Label id={p.audience} claim="P2C01" />
      </div>
    </div>)}</div><ScreenshotDisclosure desktopNote />
  </aside>;
}
export function Scope() {
  return <div className="wrap scope"><Label id="scope" className="eyebrow" /><Claim id="C06" /></div>;
}
export function Introductions() {
  const { t } = useMarketing();
  return <section className="section wrap introductions" id="products" tabIndex="-1">
    <div className="section-head"><div><Label id="four" className="eyebrow" /><h2>{t('choose')}</h2></div><Claim id="C43" /></div>
    <div className="product-intros">{products.map(p => <article className={`intro intro-${p.key}`} key={p.key}>
      <div className="intro-art"><ProductScreenshot product={p.key} compact disclosure={false} /></div>
      <div className="intro-body"><bdi className="intro-number" dir="ltr">{p.n}</bdi>
        <div className="intro-heading"><Label id={p.audience} className="eyebrow" claim="P2C01" /><h3><Mixed text={t(p.key)} /></h3></div>
        <div className="intro-copy"><Claim id={p.desc} /><PageLink page={p.key}>{t('productstudy')}</PageLink></div>
      </div>
    </article>)}</div><ScreenshotDisclosure desktopNote />
  </section>;
}
export function Relationships() {
  const { t, page } = useMarketing();
  const groups = [['relation1', ['merchant', 'supportedstore', 'eligiblehub']], ['relation2', ['eligibleproduct', 'link', 'attributed']], ['relation3', ['accounts', 'orders', 'commissions']]];
  return <section className="section relationships" id="relationships"><div className="wrap">
    <div className="section-head"><div><Label id="platform" className="eyebrow" /><h2>{t('ecosystem')}</h2></div><Claim id="C06" /></div>
    <div className="relation-grid">{groups.map(([heading, items], i) => <article className="relation" key={heading} data-claim-fragment="P2C01">
      <div className="relation-label"><bdi dir="ltr">0{i + 1}</bdi><h3>{t(heading)}</h3></div>
      <ul>{items.map(id => <Label key={id} id={id} as="li" />)}</ul>
    </article>)}</div>
    <div className="boundary"><Claim id="C31" /><Claim id="C35" /></div>
    {page !== 'platform' && <PageLink page="platform">{t('platformstudy')}</PageLink>}
  </div></section>;
}
export function Capabilities() {
  const { t } = useMarketing();
  return <section className="section wrap" id="capabilities"><div className="capabilities">
    <div className="cap-heading"><Label id="capabilities" className="eyebrow" /><h2>{t('captitle')}</h2><ProductScreenshot product="pos" compact /></div>
    <div className="cap-list">{['C08', 'C09', 'C14'].map((id, i) => <article key={id}>
      <bdi className="index" dir="ltr">0{i + 1}</bdi><Claim id={id} />
    </article>)}</div>
  </div></section>;
}
export function Reservation({ product: p, priority = false }) {
  return <ProductScreenshot product={p.key} priority={priority} />;
}
export function Proof() {
  const { t } = useMarketing();
  return <section className="section proof" id="proof"><div className="wrap">
    <div className="section-head"><div><Label id="four" className="eyebrow" /><h2>{t('choose')}</h2></div></div>
    <div className="proof-composition">{products.map(p => <article className={`proof-story story-${p.key}`} key={p.key}>
      <div className="story-copy"><span className="eyebrow"><bdi dir="ltr">{p.n}</bdi> / <Mixed text={t(p.key)} /></span>
        <Claim id={p.headline} as="h3" /><Claim id={p.proof} /><PageLink page={p.key}>{t('productstudy')}</PageLink>
      </div><Reservation product={p} />
    </article>)}</div>
  </div></section>;
}
export function Trust() {
  return <section className="section trust"><div className="wrap">
    <Label id="trust" className="eyebrow" /><Claim id="C28" as="h2" />
    <div className="trust-notes">{['C26', 'C27'].map((id, i) => <div key={id}>
      <bdi className="index" dir="ltr">0{i + 1}</bdi><Claim id={id} />
    </div>)}</div>
  </div></section>;
}
export function FAQ() {
  const { t } = useMarketing();
  const answers = [['C01'], ['C14', 'C31'], ['C32'], ['C23', 'C38'], ['C27'], ['C37']];
  return <section className="section wrap" id="faq"><div className="faq-layout">
    <h2>{t('faq')}</h2><div className="faqs">{answers.map((ids, i) => <details key={i}>
      <summary><span><Mixed text={t('questions')[i]} /></span></summary>{ids.map(id => <Claim key={id} id={id} />)}
    </details>)}</div>
  </div></section>;
}
