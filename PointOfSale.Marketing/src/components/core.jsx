import React, { createContext, useContext } from 'react';
import { ArrowRight } from 'lucide-react';
import claims from '../content/claims.json';
import labels from '../content/labels.json';
import products from '../content/products.json';
import { pagePath } from '../routes.mjs';
import { publicClaim } from '../publication.mjs';
export { claims, labels, products };
export const MarketingContext = createContext(null);
export const useMarketing = () => useContext(MarketingContext);

export function Mixed({ text }) {
  return String(text).split(/(SHOPYXI|CommerceHub|Storefront|POS Admin|Affiliate System|EGP|EN\/LTR|AR\/RTL)/g)
    .map((part, i) => /^(SHOPYXI|CommerceHub|Storefront|POS Admin|Affiliate System|EGP|EN\/LTR|AR\/RTL)$/.test(part)
      ? <bdi dir="ltr" key={i}>{part}</bdi> : part);
}
export function Claim({ id, as: Tag = 'p', className = '' }) {
  const { locale, publicRelease } = useMarketing();
  return <Tag className={className} data-claim={id} data-approval={publicRelease ? 'SOURCE_SUPPORTED' : claims[id].approval}><Mixed text={publicRelease ? publicClaim(claims, id, locale) : claims[id][locale]} /></Tag>;
}
export function Label({ id, as: Tag = 'span', className = '', claim }) {
  const { t } = useMarketing();
  return <Tag className={className} data-claim-fragment={claim}><Mixed text={t(id)} /></Tag>;
}
export function PageLink({ page, children, className = 'text-link' }) {
  const { locale, page: current } = useMarketing();
  return <a className={className} href={`${pagePath(locale, page)}#main`} aria-current={current === page ? 'page' : undefined}>
    {children}{className === 'text-link' && <ArrowRight className="arrow" aria-hidden="true" strokeWidth={1.75} />}
  </a>;
}
export function Logo() {
  const { t } = useMarketing();
  return <PageLink page="home" className="brand">
    <img src="/assets/shopyxi-logo.png" alt={`SHOPYXI — ${t('home')}`} width="2400" height="747" />
  </PageLink>;
}
export function Actions() {
  const { t, publicRelease } = useMarketing();
  if (publicRelease) return <div className="actions">
    <a className="button" href="#products"><Claim id="C40" as="span" /></a>
    <PageLink page="platform" className="button secondary">{t('platformstudy')}</PageLink>
  </div>;
  return <>
    <div className="actions">
      <a className="button" href="#demo" aria-describedby="demo-pending"><Claim id="C39" as="span" /></a>
      <a className="button secondary" href="#products"><Claim id="C40" as="span" /></a>
    </div>
    <Label id="pending" as="p" className="pending-inline" claim="P2C04" />
  </>;
}
