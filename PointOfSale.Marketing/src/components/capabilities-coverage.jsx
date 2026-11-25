import React from 'react';
import { ArrowRight, Boxes, Globe, Layers, Link2, ShoppingBag, Store, Users } from 'lucide-react';
import { Claim, Mixed, useMarketing } from './core.jsx';
import { pagePath } from '../routes.mjs';

/**
 * Phase 1F-C — Capabilities & Coverage.
 *
 * The four scenes below are original decorative vector illustrations drawn for
 * this section. They are deliberately stylised and are not product screenshots,
 * not application chrome and not evidence of a live interface. Every scene is
 * announced to assistive technology as decorative and carries no text of its own.
 */

// Reusable isometric carton used by the warehouse and marketplace scenes.
function Crate({ x, y, w, h, tone = '#e9a24c', top = '#f8cd8a', side = '#d98a35' }) {
  const px = Number(x), py = Number(y), pw = Number(w), ph = Number(h);
  const dx = Math.max(2, Math.round(pw * 0.16));
  const dy = Math.max(1, Math.round(ph * 0.1));
  return <g>
    <path d={`M${px} ${py}h${pw}l${dx} ${-dy}h${-pw}Z`} fill={top} />
    <path d={`M${px + pw} ${py}l${dx} ${-dy}v${ph}l${-dx} ${dy}Z`} fill={side} />
    <rect x={px} y={py} width={pw} height={ph} rx="2" fill={tone} />
    <rect x={px + pw * 0.44} y={py} width={Math.max(2, pw * 0.11)} height={ph} fill="#fbe6bf" opacity=".85" />
  </g>;
}

// 01 — POS Admin: two warehouses, stock cartons and transfer arrows.
function WarehouseArt() {
  return <svg viewBox="0 0 360 260" preserveAspectRatio="xMidYMid meet" aria-hidden="true" focusable="false"><defs>
    <radialGradient id="ccPosHalo" cx="50%" cy="50%" r="50%">
      <stop offset="0" stopColor="#dbeafe" /><stop offset="1" stopColor="#dbeafe" stopOpacity="0" />
    </radialGradient>
    <linearGradient id="ccPosRoof" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stopColor="#6aa2fb" /><stop offset="1" stopColor="#2f6fe4" />
    </linearGradient>
  </defs>
  <ellipse cx="240" cy="132" rx="116" ry="98" fill="url(#ccPosHalo)" />
  <ellipse cx="192" cy="234" rx="128" ry="14" fill="#c7ddff" opacity=".55" />

  {/* smaller warehouse, inline start */}
  <path d="M68 160 136 121 204 160Z" fill="url(#ccPosRoof)" stroke="#2f6fe4" strokeWidth="3" strokeLinejoin="round" />
  <rect x="126" y="112" width="18" height="13" rx="3" fill="#2f6fe4" />
  <rect x="80" y="158" width="112" height="66" rx="6" fill="#f4f8ff" stroke="#a9c7f7" strokeWidth="2.5" />
  <rect x="112" y="174" width="48" height="50" rx="3" fill="#24406f" />
  <Crate x="118" y="190" w="16" h="24" />
  <Crate x="137" y="190" w="16" h="24" />
  <Crate x="127" y="173" w="18" h="17" />

  {/* larger warehouse, inline end */}
  <path d="M192 104 268 60 344 104Z" fill="url(#ccPosRoof)" stroke="#2f6fe4" strokeWidth="3" strokeLinejoin="round" />
  <rect x="258" y="51" width="20" height="14" rx="3" fill="#2f6fe4" />
  <rect x="204" y="102" width="128" height="122" rx="7" fill="#f4f8ff" stroke="#a9c7f7" strokeWidth="2.5" />
  <rect x="246" y="124" width="70" height="84" rx="3" fill="#24406f" />
  <Crate x="251" y="166" w="26" h="42" />
  <Crate x="282" y="166" w="26" h="42" />
  <Crate x="266" y="130" w="26" h="34" />

  {/* directional transfer arrows */}
  <path d="M120 112c14-26 48-40 82-34" fill="none" stroke="#7aa8f7" strokeWidth="7" strokeLinecap="round" />
  <path d="M196 66l20 13-22 11" fill="none" stroke="#7aa8f7" strokeWidth="7" strokeLinecap="round" strokeLinejoin="round" />
  <path d="M252 231c-28 11-66 10-96-1" fill="none" stroke="#2f6fe4" strokeWidth="7" strokeLinecap="round" />
  <path d="M160 218l-23 11 24 12" fill="none" stroke="#2f6fe4" strokeWidth="7" strokeLinecap="round" strokeLinejoin="round" />
  <circle cx="300" cy="54" r="4" fill="#93c5fd" />
  <circle cx="92" cy="98" r="3" fill="#bfdbfe" />
  </svg>;
}

// 02 — Storefront: browser window, striped awning, bag and content blocks.
function StorefrontArt() {
  return <svg viewBox="0 0 360 260" preserveAspectRatio="xMidYMid meet" aria-hidden="true" focusable="false"><defs>
    <radialGradient id="ccStoreHalo" cx="50%" cy="50%" r="50%">
      <stop offset="0" stopColor="#ede9fe" /><stop offset="1" stopColor="#ede9fe" stopOpacity="0" />
    </radialGradient>
    <linearGradient id="ccStoreTop" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stopColor="#8b5cf6" /><stop offset="1" stopColor="#6d28d9" />
    </linearGradient>
  </defs>
  <ellipse cx="238" cy="132" rx="112" ry="96" fill="url(#ccStoreHalo)" />
  <rect x="72" y="66" width="236" height="172" rx="16" fill="#7c3aed" opacity=".12" />
  <rect x="70" y="58" width="236" height="172" rx="14" fill="#ffffff" stroke="#c4b5fd" strokeWidth="2" />
  <path d="M70 72a14 14 0 0 1 14-14h208a14 14 0 0 1 14 14v22H70Z" fill="url(#ccStoreTop)" />
  <circle cx="88" cy="72" r="4" fill="#fbbf24" />
  <circle cx="102" cy="72" r="4" fill="#f472b6" />
  <circle cx="116" cy="72" r="4" fill="#34d399" />

  {/* striped awning */}
  {[0, 1, 2, 3, 4, 5, 6, 7].map(i => {
    const x = 82 + i * 26.5;
    return <path key={i} d={`M${x} 94h26.5v14a13.25 13.25 0 0 1-26.5 0Z`}
      fill={i % 2 === 0 ? '#8b5cf6' : '#f5f3ff'} />;
  })}

  {/* shopping bag */}
  <path d="M105 140h43l-4 44a6 6 0 0 1-6 6h-23a6 6 0 0 1-6-6Z" fill="#a78bfa" stroke="#7c3aed" strokeWidth="2.5" strokeLinejoin="round" />
  <path d="M114 140v-8a12 12 0 0 1 24 0v8" fill="none" stroke="#7c3aed" strokeWidth="3" strokeLinecap="round" />
  <path d="M113 152h27" stroke="#c4b5fd" strokeWidth="3" strokeLinecap="round" />

  {/* content blocks */}
  <rect x="170" y="138" width="112" height="10" rx="5" fill="#ddd6fe" />
  <rect x="170" y="156" width="86" height="10" rx="5" fill="#ede9fe" />
  <rect x="170" y="174" width="100" height="10" rx="5" fill="#ede9fe" />
  <rect x="88" y="194" width="48" height="28" rx="6" fill="#ede9fe" stroke="#ddd6fe" strokeWidth="1.5" />
  <path d="M96 216l9-9 7 7 4-4 8 8Z" fill="#a78bfa" />
  <circle cx="101" cy="202" r="3" fill="#c4b5fd" />
  <rect x="146" y="196" width="58" height="8" rx="4" fill="#ddd6fe" />
  <rect x="146" y="210" width="42" height="8" rx="4" fill="#ede9fe" />
  <rect x="214" y="196" width="70" height="8" rx="4" fill="#ddd6fe" />
  <rect x="214" y="210" width="54" height="8" rx="4" fill="#ede9fe" />
  <circle cx="326" cy="116" r="4" fill="#c4b5fd" />
  <circle cx="54" cy="128" r="3" fill="#ddd6fe" />
  </svg>;
}

// 03 — Affiliate System: laptop link, megaphone, checklist and percent badge.
function AffiliateArt() {
  const rows = [0, 1, 2];
  return <svg viewBox="0 0 360 260" preserveAspectRatio="xMidYMid meet" aria-hidden="true" focusable="false"><defs>
    <radialGradient id="ccAffHalo" cx="50%" cy="50%" r="50%">
      <stop offset="0" stopColor="#d1fae5" /><stop offset="1" stopColor="#d1fae5" stopOpacity="0" />
    </radialGradient>
  </defs>
  <ellipse cx="214" cy="132" rx="124" ry="98" fill="url(#ccAffHalo)" />
  <ellipse cx="160" cy="236" rx="116" ry="12" fill="#a7f3d0" opacity=".5" />

  {/* laptop */}
  <path d="M38 214h198l12 10a6 6 0 0 1-6 6H32a6 6 0 0 1-6-6Z" fill="#0b5a43" />
  <rect x="46" y="98" width="180" height="118" rx="11" fill="#0f6b4f" />
  <rect x="57" y="109" width="158" height="96" rx="4" fill="#f0fdf4" />
  <rect x="57" y="109" width="158" height="18" rx="4" fill="#dcfce7" />
  <circle cx="67" cy="118" r="2.5" fill="#34d399" />
  <circle cx="76" cy="118" r="2.5" fill="#6ee7b7" />
  <circle cx="85" cy="118" r="2.5" fill="#a7f3d0" />
  {/* chain links */}
  <rect x="96" y="146" width="38" height="18" rx="9" fill="none" stroke="#059669" strokeWidth="6" transform="rotate(-45 115 155)" />
  <rect x="118" y="160" width="38" height="18" rx="9" fill="none" stroke="#059669" strokeWidth="6" transform="rotate(-45 137 169)" />
  <path d="M74 190h52M136 190h34" stroke="#a7f3d0" strokeWidth="5" strokeLinecap="round" />

  {/* megaphone */}
  <path d="M246 96l46-20v54l-46-20Z" fill="#34d399" stroke="#059669" strokeWidth="2.5" strokeLinejoin="round" />
  <ellipse cx="292" cy="103" rx="7" ry="27" fill="#10b981" stroke="#059669" strokeWidth="2.5" />
  <rect x="236" y="93" width="12" height="20" rx="5" fill="#059669" />
  <path d="M252 110v26a7 7 0 0 0 14 0v-14" fill="#059669" />
  <path d="M306 88q9 15 0 30M315 81q12 22 0 44M324 74q15 29 0 58" fill="none" stroke="#34d399" strokeWidth="3.5" strokeLinecap="round" />

  {/* commission checklist */}
  <rect x="210" y="158" width="108" height="86" rx="11" fill="#ffffff" stroke="#bbf7d0" strokeWidth="2" />
  <rect x="222" y="168" width="84" height="8" rx="4" fill="#86efac" />
  {rows.map(i => <g key={i}>
    <circle cx="228" cy="187" r="6" fill="#16a34a" />
    <path d={`M225 ${187}l2 2 4-5`} fill="none" stroke="#ffffff" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
    <rect x="240" y="183" width="56" height="6" rx="3" fill="#d1fae5" />
  </g>)}

  {/* percent badge */}
  <circle cx="322" cy="214" r="27" fill="#fbbf24" stroke="#f59e0b" strokeWidth="3" />
  <circle cx="314" cy="205" r="4.5" fill="none" stroke="#ffffff" strokeWidth="3" />
  <circle cx="330" cy="223" r="4.5" fill="none" stroke="#ffffff" strokeWidth="3" />
  <path d="M312 226l20-24" stroke="#ffffff" strokeWidth="3" strokeLinecap="round" />
  <circle cx="70" cy="78" r="4" fill="#6ee7b7" />
  <circle cx="142" cy="86" r="3" fill="#a7f3d0" />
  </svg>;
}

// 04 — CommerceHub: listing window, product, category tag and approval mark.
function MarketplaceArt() {
  return <svg viewBox="0 0 360 260" preserveAspectRatio="xMidYMid meet" aria-hidden="true" focusable="false"><defs>
    <radialGradient id="ccHubHalo" cx="50%" cy="50%" r="50%">
      <stop offset="0" stopColor="#ffedd5" /><stop offset="1" stopColor="#ffedd5" stopOpacity="0" />
    </radialGradient>
    <linearGradient id="ccHubTop" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stopColor="#c2622a" /><stop offset="1" stopColor="#a24c1c" />
    </linearGradient>
  </defs>
  <ellipse cx="236" cy="134" rx="114" ry="96" fill="url(#ccHubHalo)" />
  <rect x="60" y="72" width="246" height="164" rx="15" fill="#b45309" opacity=".12" />
  <rect x="58" y="64" width="246" height="164" rx="13" fill="#ffffff" stroke="#f3c99b" strokeWidth="2" />
  <path d="M58 77a13 13 0 0 1 13-13h220a13 13 0 0 1 13 13v21H58Z" fill="url(#ccHubTop)" />
  <circle cx="76" cy="81" r="4" fill="#fbbf24" />
  <circle cx="90" cy="81" r="4" fill="#fb923c" />
  <circle cx="104" cy="81" r="4" fill="#f87171" />

  {/* listing image and attributes */}
  <rect x="76" y="112" width="78" height="78" rx="8" fill="#fdf5ea" stroke="#f3d3ad" strokeWidth="1.5" />
  <ellipse cx="115" cy="178" rx="26" ry="4" fill="#f5dcb9" />
  <Crate x="97" y="137" w="34" h="34" />
  <rect x="176" y="120" width="102" height="9" rx="4.5" fill="#e8eef8" />
  <rect x="176" y="138" width="78" height="9" rx="4.5" fill="#eef2f9" />
  <rect x="176" y="156" width="94" height="9" rx="4.5" fill="#eef2f9" />

  {/* category tag */}
  <rect x="176" y="180" width="82" height="22" rx="11" fill="#ffedd5" stroke="#fdba74" strokeWidth="1.5" />
  <path d="M187 186h5l5 5-5 5h-5Z" fill="#ea580c" />
  <circle cx="190" cy="191" r="1.4" fill="#ffedd5" />
  <rect x="200" y="187" width="42" height="7" rx="3.5" fill="#fdba74" />
  <rect x="76" y="204" width="140" height="8" rx="4" fill="#e8eef8" />
  <rect x="76" y="219" width="98" height="8" rx="4" fill="#eef2f9" />

  {/* approval mark */}
  <circle cx="302" cy="214" r="24" fill="#16a34a" />
  <path d="M291 214l8 8 15-17" fill="none" stroke="#ffffff" strokeWidth="5" strokeLinecap="round" strokeLinejoin="round" />
  <circle cx="330" cy="104" r="4" fill="#fdba74" />
  <circle cx="44" cy="150" r="3" fill="#fed7aa" />
  </svg>;
}

const CARDS = [
  { key: 'pos', accent: 'blue', Icon: Boxes, title: 'ccPosTitle', tag: 'ccTagPos', claim: 'C09', Art: WarehouseArt },
  { key: 'storefront', accent: 'purple', Icon: ShoppingBag, title: 'ccStoreTitle', tag: 'ccTagStore', claim: 'C20', Art: StorefrontArt },
  { key: 'affiliate', accent: 'green', Icon: Users, title: 'ccAffiliateTitle', tag: 'ccTagAffiliate', claim: 'C04', Art: AffiliateArt },
  { key: 'commercehub', accent: 'orange', Icon: Store, title: 'ccCommercehubTitle', tag: 'ccTagCommercehub', claim: 'C33', Art: MarketplaceArt }
];

function CoverageCard({ card }) {
  const { t } = useMarketing();
  const { key, accent, Icon, title, tag, claim, Art } = card;
  const titleId = `cc-${key}-title`;
  return <article className={`cc-card cc-card-${accent}`} data-cc-card={key} aria-labelledby={titleId}>
    <div className="cc-card-copy">
      <div className="cc-meta">
        <span className="cc-icon" aria-hidden="true"><Icon size={22} strokeWidth={1.9} /></span>
        <span className="cc-tag"><Mixed text={t(tag)} /></span>
      </div>
      <h3 className="cc-card-title" id={titleId}>{t(title)}</h3>
      <Claim id={claim} as="p" className="cc-card-body" />
    </div>
    <div className="cc-art" aria-hidden="true">
      <Art />
    </div>
  </article>;
}

export function CapabilitiesCoverageSection() {
  const { t, locale } = useMarketing();
  // Keep the trailing arrow attached to the final word so it never wraps alone.
  const linkLabel = t('ccLink');
  const linkBreak = linkLabel.lastIndexOf(' ');
  const linkLead = linkBreak > 0 ? linkLabel.slice(0, linkBreak + 1) : '';
  const linkTail = linkBreak > 0 ? linkLabel.slice(linkBreak + 1) : linkLabel;
  return <section className="cc-section" id="coverage" aria-labelledby="cc-title">
    <span className="cc-orb cc-orb-a" aria-hidden="true" />
    <span className="cc-orb cc-orb-b" aria-hidden="true" />
    <div className="cc-shell">
      <header className="cc-intro">
        <span className="cc-badge"><Layers size={18} strokeWidth={2.1} aria-hidden="true" /><Mixed text={t('ccBadge')} /></span>
        <h2 className="cc-title" id="cc-title"><Claim id="C50" as="span" /></h2>
        <Claim id="C51" as="p" className="cc-sub" />
      </header>
      <div className="cc-grid">
        {CARDS.map(card => <CoverageCard key={card.key} card={card} />)}
      </div>
      <div className="cc-panel">
        <div className="cc-half cc-half-language">
          <span className="cc-panel-icon cc-panel-icon-language" aria-hidden="true"><Globe size={28} strokeWidth={1.8} /></span>
          <div className="cc-half-copy">
            <h3>{t('ccLangHeading')}</h3>
            <Claim id="C26" as="p" />
          </div>
          <ul className="cc-langs">
            <li><span className="cc-lang" lang="ar" dir="rtl">{t('ccArabic')}</span></li>
            <li><span className="cc-lang" lang="en" dir="ltr">{t('ccEnglish')}</span></li>
          </ul>
        </div>
        <div className="cc-half cc-half-integrations">
          <span className="cc-panel-icon cc-panel-icon-integrations" aria-hidden="true"><Link2 size={28} strokeWidth={1.8} /></span>
          <div className="cc-half-copy">
            <h3>{t('ccIntegrationsHeading')}</h3>
            <Claim id="C27a" as="p" />
            <a className="cc-link" href={`${pagePath(locale, 'platform')}#integrations`}>
              <span className="cc-link-label">
                <Mixed text={linkLead} />
                <span className="cc-link-tail">
                  <Mixed text={linkTail} />
                  <ArrowRight className="cc-link-arrow" size={18} strokeWidth={2.2} aria-hidden="true" />
                </span>
              </span>
            </a>
          </div>
        </div>
      </div>
    </div>
  </section>;
}
