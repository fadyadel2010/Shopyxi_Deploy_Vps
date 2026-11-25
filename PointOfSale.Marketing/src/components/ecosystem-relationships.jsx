import React from 'react';
import {
  Boxes, Layers, ReceiptText, ShoppingBag, ShoppingCart, Store, Users
} from 'lucide-react';
import { Claim, useMarketing } from './core.jsx';

/**
 * Phase 1C — Ecosystem Relationships.
 *
 * Six approved product-relationship cards in a fixed 3 x 2 composition. The
 * section is informational: no links, no controls and no application capture.
 * Every illustration below is an original inline SVG or CSS composition drawn
 * for this section, so nothing can be read as a live SHOPYXI screen or as
 * operational data (no prices, volumes, commissions or approval outcomes).
 */

const BRAND = {
  pos: 'POS Admin',
  storefront: 'Storefront',
  affiliate: 'Affiliate System',
  commercehub: 'CommerceHub'
};

const CHIP_ICON = { pos: ReceiptText, storefront: Store, affiliate: Users, commercehub: ShoppingBag };

// Latin brand tokens stay isolated inside Arabic copy with bdi, following the
// existing localization convention used by Mixed.
const TOKENS = ['Affiliate System', 'CommerceHub', 'POS Admin', 'Storefront', 'SHOPYXI', 'Affiliate'];

function Isolated({ text }) {
  const parts = String(text).split(new RegExp('(' + TOKENS.join('|') + ')', 'g'));
  return <>{parts.map((part, index) => (TOKENS.includes(part)
    ? <bdi dir="ltr" key={index}>{part}</bdi>
    : <React.Fragment key={index}>{part}</React.Fragment>))}</>;
}

const CARDS = [
  { n: '01', accent: 'blue', icon: Boxes, title: 'er1title', desc: 'er1desc', chips: ['pos', 'storefront', 'commercehub'], art: 'catalog' },
  { n: '02', accent: 'violet', icon: Store, title: 'er2title', desc: 'er2desc', chips: ['pos', 'storefront'], art: 'configuration' },
  { n: '03', accent: 'green', icon: Users, title: 'er3title', desc: 'er3desc', chips: ['affiliate', 'storefront'], art: 'affiliate' },
  { n: '04', accent: 'amber', icon: ShoppingBag, title: 'er4title', desc: 'er4desc', chips: ['pos', 'commercehub'], art: 'publishing' },
  { n: '05', accent: 'green', icon: ShoppingCart, title: 'er5title', desc: 'er5desc', chips: ['storefront', 'commercehub'], art: 'orders' },
  { n: '06', accent: 'amber', icon: Users, title: 'er6title', desc: 'er6desc', chips: ['pos', 'storefront', 'affiliate', 'commercehub'], art: 'accounts' }
];

const ART = {
  catalog: ArtCatalog,
  configuration: ArtConfiguration,
  affiliate: ArtAffiliate,
  publishing: ArtPublishing,
  orders: ArtOrders,
  accounts: ArtAccounts
};

export function EcosystemRelationships() {
  const { t } = useMarketing();
  return <section className="er-section" id="relationships" aria-labelledby="er-heading">
    <div className="er-shell">
      <header className="er-intro">
        <p className="er-eyebrow">
          <Layers aria-hidden="true" size={17} strokeWidth={2} />
          <span><Isolated text={t('ereyebrow')} /></span>
        </p>
        <h2 id="er-heading"><Isolated text={t('erheading')} /></h2>
        <p className="er-sub"><Isolated text={t('ersub')} /></p>
      </header>
      <ul className="er-grid">
        {CARDS.map(card => <RelationshipCard key={card.n} card={card} />)}
      </ul>
      <div className="er-bounds">
        <h3 className="er-bounds-title">{t('erboundary')}</h3>
        <div className="er-bounds-list">
          <Claim id="C06" />
          <Claim id="C31" />
          <Claim id="C35" />
        </div>
      </div>
    </div>
  </section>;
}

function RelationshipCard({ card }) {
  const { t } = useMarketing();
  const Icon = card.icon;
  const Art = ART[card.art];
  return <li className={`er-item er-accent-${card.accent}`}>
    <article className="er-card">
      <div className="er-card-body">
        <div className="er-head">
          <div className="er-heading">
            <span className="er-number" dir="ltr">{card.n}</span>
            <h3 className="er-title"><Isolated text={t(card.title)} /></h3>
          </div>
          <span className="er-icon" aria-hidden="true"><Icon size={22} strokeWidth={1.9} /></span>
        </div>
        <ul className="er-chips">
          {card.chips.map(key => <li key={key} className={`er-chip er-chip-${key}`}>
            {React.createElement(CHIP_ICON[key], { size: 13, strokeWidth: 2, 'aria-hidden': true })}
            <bdi dir="ltr">{BRAND[key]}</bdi>
          </li>)}
        </ul>
        <p className="er-desc"><Isolated text={t(card.desc)} /></p>
        {card.art === 'accounts'
          ? <Art />
          : <div className="er-art" aria-hidden="true"><Art /></div>}
      </div>
    </article>
  </li>;
}
/* ------------------------------------------------------------------ art 01 */
function ArtCatalog() {
  return <svg className="er-art-svg" viewBox="0 0 320 132" aria-hidden="true" focusable="false">
    <defs>
      <radialGradient id="er-a1-glow" cx="50%" cy="50%" r="50%">
        <stop offset="0%" stopColor="#3b82f6" stopOpacity=".45" />
        <stop offset="100%" stopColor="#3b82f6" stopOpacity="0" />
      </radialGradient>
      <linearGradient id="er-a1-stage" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0%" stopColor="#3b82f6" stopOpacity=".62" />
        <stop offset="100%" stopColor="#0f2a5c" stopOpacity=".05" />
      </linearGradient>
      <linearGradient id="er-a1-panel" x1="0" y1="0" x2="1" y2="1">
        <stop offset="0%" stopColor="#f3f7ff" />
        <stop offset="100%" stopColor="#c6d9f7" />
      </linearGradient>
      <linearGradient id="er-a1-box" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0%" stopColor="#e6b17a" />
        <stop offset="100%" stopColor="#b9783c" />
      </linearGradient>
    </defs>
    <ellipse cx="160" cy="116" rx="136" ry="18" fill="url(#er-a1-glow)" />
    <ellipse cx="160" cy="112" rx="118" ry="8" fill="url(#er-a1-stage)" />
    <path d="M62 40 L72 33 h36 l-10 7 Z" fill="#eec08d" />
    <rect x="62" y="40" width="36" height="26" rx="2.5" fill="url(#er-a1-box)" />
    <path d="M34 62 L44 55 h38 l-10 7 Z" fill="#eec08d" />
    <rect x="34" y="62" width="38" height="46" rx="3" fill="url(#er-a1-box)" />
    <path d="M53 62 v46" stroke="#a5682f" strokeWidth="4" strokeOpacity=".45" />
    <path d="M86 84 L94 78 h30 l-8 6 Z" fill="#eec08d" />
    <rect x="86" y="84" width="30" height="24" rx="2.5" fill="url(#er-a1-box)" />
    <rect x="126" y="22" width="172" height="90" rx="10" fill="url(#er-a1-panel)" stroke="#93b6ea" strokeWidth="1.1" />
    <rect x="138" y="32" width="54" height="5" rx="2.5" fill="#90afd9" />
    <circle cx="278" cy="34.5" r="2" fill="#90afd9" />
    <circle cx="286" cy="34.5" r="2" fill="#90afd9" />
    <g transform="translate(138 44)">
      <rect width="46" height="54" rx="7" fill="#ffffff" stroke="#c3d6f0" />
      <rect x="14" y="12" width="17" height="19" rx="3" fill="#d2554f" />
      <path d="M31 17 h3.4 a4.2 4.2 0 0 1 0 8.4 H31" fill="none" stroke="#d2554f" strokeWidth="2.2" />
      <rect x="10" y="41" width="26" height="4" rx="2" fill="#c9d9ef" />
    </g>
    <g transform="translate(192 44)">
      <rect width="46" height="54" rx="7" fill="#ffffff" stroke="#c3d6f0" />
      <rect x="19" y="9" width="8" height="6" rx="1.8" fill="#3f8f5a" />
      <rect x="15" y="15" width="16" height="19" rx="4.5" fill="#3f8f5a" />
      <rect x="10" y="41" width="26" height="4" rx="2" fill="#c9d9ef" />
    </g>
    <g transform="translate(246 44)">
      <rect width="46" height="54" rx="7" fill="#ffffff" stroke="#c3d6f0" />
      <path d="M13 18 h20 l-2 17 H15 Z" fill="#d97a2b" />
      <path d="M18 18 v-3 a5 5 0 0 1 10 0 v3" fill="none" stroke="#b3611d" strokeWidth="2" />
      <rect x="10" y="41" width="26" height="4" rx="2" fill="#c9d9ef" />
    </g>
  </svg>;
}

/* ------------------------------------------------------------------ art 02 */
function ArtConfiguration() {
  return <svg className="er-art-svg" viewBox="0 0 320 132" aria-hidden="true" focusable="false">
    <defs>
      <radialGradient id="er-a2-glow" cx="50%" cy="50%" r="50%">
        <stop offset="0%" stopColor="#8b5cf6" stopOpacity=".42" />
        <stop offset="100%" stopColor="#8b5cf6" stopOpacity="0" />
      </radialGradient>
      <linearGradient id="er-a2-stage" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0%" stopColor="#8b5cf6" stopOpacity=".6" />
        <stop offset="100%" stopColor="#271a55" stopOpacity=".05" />
      </linearGradient>
      <linearGradient id="er-a2-panel" x1="0" y1="0" x2="1" y2="1">
        <stop offset="0%" stopColor="#3b2a7d" />
        <stop offset="100%" stopColor="#241a52" />
      </linearGradient>
      <clipPath id="er-a2-win"><rect x="150" y="14" width="152" height="98" rx="11" /></clipPath>
    </defs>
    <ellipse cx="160" cy="116" rx="136" ry="18" fill="url(#er-a2-glow)" />
    <ellipse cx="160" cy="112" rx="118" ry="8" fill="url(#er-a2-stage)" />
    <rect x="18" y="26" width="122" height="86" rx="12" fill="url(#er-a2-panel)" stroke="#8b6ff0" strokeWidth="1.2" />
    <g transform="translate(46 50)">
      <circle r="10" fill="#c4b5fd" fillOpacity=".16" />
      <circle r="6.2" fill="none" stroke="#c4b5fd" strokeWidth="2.4" />
      <circle r="1.8" fill="#c4b5fd" />
      {[0, 45, 90, 135].map(angle => <rect key={angle} x="-1.2" y="-11.5" width="2.4" height="4.4" rx="1.2" fill="#c4b5fd" transform={`rotate(${angle})`} />)}
    </g>
    <rect x="36" y="70" width="86" height="5" rx="2.5" fill="#a78bfa" fillOpacity=".9" />
    <rect x="36" y="81" width="62" height="5" rx="2.5" fill="#7f6fbe" />
    <rect x="36" y="92" width="58" height="5" rx="2.5" fill="#6f61a8" />
    <rect x="106" y="87" width="28" height="12" rx="6" fill="#a78bfa" />
    <circle cx="128" cy="93" r="4.2" fill="#f5f3ff" />
    <g clipPath="url(#er-a2-win)">
      <rect x="150" y="14" width="152" height="98" fill="#0d1730" />
      <rect x="150" y="14" width="152" height="18" fill="#0a1124" />
      <circle cx="160" cy="23" r="2" fill="#ff5f57" />
      <circle cx="167" cy="23" r="2" fill="#febc2e" />
      <circle cx="174" cy="23" r="2" fill="#28c840" />
      {[0, 1, 2, 3, 4, 5, 6, 7].map(i => <rect key={i} x={150 + i * 19} y="32" width="19" height="14" fill={i % 2 ? '#efe9ff' : '#7c4dff'} />)}
      <rect x="150" y="45" width="152" height="2.4" fill="#0a1124" fillOpacity=".35" />
      <rect x="169" y="58" width="52" height="46" rx="6" fill="#f4f7ff" />
      <rect x="231" y="58" width="52" height="46" rx="6" fill="#f4f7ff" />
      <g transform="translate(184 70)">
        <rect width="17" height="19" rx="3" fill="#d2554f" />
        <path d="M17 5 h3.4 a4 4 0 0 1 0 8 H17" fill="none" stroke="#d2554f" strokeWidth="2.2" />
      </g>
      <g transform="translate(245 70)">
        <path d="M0 6 h20 l-2 17 H2 Z" fill="#d97a2b" />
        <path d="M5 6 V3 a5 5 0 0 1 10 0 v3" fill="none" stroke="#b3611d" strokeWidth="2" />
      </g>
    </g>
    <rect x="150" y="14" width="152" height="98" rx="11" fill="none" stroke="#7c5cff" strokeWidth="1.3" />
  </svg>;
}
/* ------------------------------------------------------------------ art 03 */
function ArtAffiliate() {
  return <svg className="er-art-svg" viewBox="0 0 320 132" aria-hidden="true" focusable="false">
    <defs>
      <radialGradient id="er-a3-glow" cx="50%" cy="50%" r="50%">
        <stop offset="0%" stopColor="#10b981" stopOpacity=".4" />
        <stop offset="100%" stopColor="#10b981" stopOpacity="0" />
      </radialGradient>
      <linearGradient id="er-a3-stage" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0%" stopColor="#10b981" stopOpacity=".55" />
        <stop offset="100%" stopColor="#0c2e2c" stopOpacity=".05" />
      </linearGradient>
      <linearGradient id="er-a3-card" x1="0" y1="0" x2="1" y2="1">
        <stop offset="0%" stopColor="#0d5b5e" />
        <stop offset="100%" stopColor="#0f766e" />
      </linearGradient>
      <clipPath id="er-a3-win"><rect x="150" y="22" width="152" height="90" rx="10" /></clipPath>
    </defs>
    <ellipse cx="160" cy="116" rx="136" ry="18" fill="url(#er-a3-glow)" />
    <ellipse cx="160" cy="112" rx="118" ry="8" fill="url(#er-a3-stage)" />
    <rect x="18" y="30" width="108" height="82" rx="13" fill="url(#er-a3-card)" stroke="#2dd4bf" strokeWidth="1.1" />
    <circle cx="50" cy="50" r="11" fill="#d1fae5" />
    <circle cx="50" cy="46" r="4" fill="#0f766e" />
    <path d="M42 57 a8 8 0 0 1 16 0 Z" fill="#0f766e" />
    <rect x="34" y="72" width="76" height="5" rx="2.5" fill="#99f6e4" fillOpacity=".7" />
    <rect x="34" y="84" width="56" height="5" rx="2.5" fill="#5eead4" fillOpacity=".45" />
    <rect x="34" y="96" width="66" height="5" rx="2.5" fill="#5eead4" fillOpacity=".35" />
    <g transform="rotate(-8 118 96)">
      <rect x="96" y="74" width="44" height="44" rx="12" fill="#f8fafc" stroke="#dbe3ee" />
      <g fill="none" stroke="#2563eb" strokeWidth="3.2" strokeLinecap="round">
        <path d="M112 98 l-4.5 4.5 a6 6 0 0 0 8.5 8.5 l4.5 -4.5" />
        <path d="M124 102 l4.5 -4.5 a6 6 0 0 0 -8.5 -8.5 l-4.5 4.5" />
        <path d="M113 107 l10 -10" />
      </g>
    </g>
    <path d="M142 96 h6" stroke="#34d399" strokeWidth="1.6" strokeDasharray="3 3" strokeLinecap="round" />
    <g clipPath="url(#er-a3-win)">
      <rect x="150" y="22" width="152" height="90" fill="#0d1730" />
      <rect x="150" y="22" width="152" height="18" fill="#0a1124" />
      <circle cx="160" cy="31" r="2" fill="#ff5f57" />
      <circle cx="167" cy="31" r="2" fill="#febc2e" />
      <circle cx="174" cy="31" r="2" fill="#28c840" />
      {[0, 1, 2, 3, 4, 5, 6, 7].map(i => <rect key={i} x={150 + i * 19} y="40" width="19" height="13" fill={i % 2 ? '#efe9ff' : '#7c4dff'} />)}
      <rect x="150" y="52" width="152" height="2.4" fill="#0a1124" fillOpacity=".35" />
      <rect x="169" y="62" width="52" height="42" rx="6" fill="#f4f7ff" />
      <rect x="231" y="62" width="52" height="42" rx="6" fill="#f4f7ff" />
      <g transform="translate(184 74)">
        <rect width="17" height="18" rx="3" fill="#d2554f" />
        <path d="M17 4 h3.4 a4 4 0 0 1 0 8 H17" fill="none" stroke="#d2554f" strokeWidth="2.2" />
      </g>
      <g transform="translate(245 74)">
        <path d="M0 5 h20 l-2 16 H2 Z" fill="#d97a2b" />
        <path d="M5 5 V2 a5 5 0 0 1 10 0 v3" fill="none" stroke="#b3611d" strokeWidth="2" />
      </g>
    </g>
    <rect x="150" y="22" width="152" height="90" rx="10" fill="none" stroke="#7c5cff" strokeWidth="1.3" />
  </svg>;
}

/* ------------------------------------------------------------------ art 04 */
function ArtPublishing() {
  return <svg className="er-art-svg" viewBox="0 0 320 132" aria-hidden="true" focusable="false">
    <defs>
      <radialGradient id="er-a4-glow" cx="50%" cy="50%" r="50%">
        <stop offset="0%" stopColor="#f59e0b" stopOpacity=".4" />
        <stop offset="100%" stopColor="#f59e0b" stopOpacity="0" />
      </radialGradient>
      <linearGradient id="er-a4-stage" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0%" stopColor="#f59e0b" stopOpacity=".55" />
        <stop offset="100%" stopColor="#3a2606" stopOpacity=".05" />
      </linearGradient>
    </defs>
    <ellipse cx="160" cy="116" rx="136" ry="18" fill="url(#er-a4-glow)" />
    <ellipse cx="160" cy="112" rx="118" ry="8" fill="url(#er-a4-stage)" />
    <rect x="20" y="22" width="122" height="92" rx="9" fill="#eef3f9" stroke="#cbd6e4" />
    <rect x="64" y="14" width="34" height="16" rx="5" fill="#9aa8ba" />
    <rect x="70" y="18" width="22" height="8" rx="4" fill="#6b7a8f" />
    {[0, 1, 2].map(i => <g key={i} transform={`translate(34 ${44 + i * 22})`}>
      <rect width="15" height="15" rx="4" fill="#16a34a" />
      <path d="M3.5 7.5 l3 3 l5 -6" fill="none" stroke="#ffffff" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
      <rect x="22" y="4" width="66" height="6" rx="3" fill="#cfd9e6" />
    </g>)}
    <rect x="152" y="28" width="148" height="80" rx="10" fill="#ffffff" stroke="#e7d4b4" />
    <path d="M166 62 L176 55 h30 l-10 7 Z" fill="#eec08d" />
    <rect x="166" y="62" width="30" height="26" rx="2.5" fill="#c98a4b" />
    <path d="M181 62 v26" stroke="#a5682f" strokeWidth="3" strokeOpacity=".45" />
    <rect x="210" y="44" width="76" height="6" rx="3" fill="#dbe3ee" />
    <rect x="210" y="58" width="62" height="6" rx="3" fill="#dbe3ee" />
    <rect x="210" y="72" width="48" height="6" rx="3" fill="#dbe3ee" />
    <circle cx="274" cy="92" r="16" fill="#ffffff" />
    <circle cx="274" cy="92" r="12.5" fill="#16a34a" />
    <path d="M268.5 92 l4 4 l7.5 -8" fill="none" stroke="#ffffff" strokeWidth="2.6" strokeLinecap="round" strokeLinejoin="round" />
  </svg>;
}
/* ------------------------------------------------------------------ art 05 */
function ArtOrders() {
  return <svg className="er-art-svg" viewBox="0 0 320 132" aria-hidden="true" focusable="false">
    <defs>
      <radialGradient id="er-a5-glowV" cx="50%" cy="50%" r="50%">
        <stop offset="0%" stopColor="#8b5cf6" stopOpacity=".4" />
        <stop offset="100%" stopColor="#8b5cf6" stopOpacity="0" />
      </radialGradient>
      <radialGradient id="er-a5-glowA" cx="50%" cy="50%" r="50%">
        <stop offset="0%" stopColor="#f59e0b" stopOpacity=".4" />
        <stop offset="100%" stopColor="#f59e0b" stopOpacity="0" />
      </radialGradient>
      <linearGradient id="er-a5-stageV" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0%" stopColor="#8b5cf6" stopOpacity=".55" />
        <stop offset="100%" stopColor="#231650" stopOpacity=".05" />
      </linearGradient>
      <linearGradient id="er-a5-stageA" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0%" stopColor="#f59e0b" stopOpacity=".55" />
        <stop offset="100%" stopColor="#3a2606" stopOpacity=".05" />
      </linearGradient>
      <linearGradient id="er-a5-store" x1="0" y1="0" x2="1" y2="1">
        <stop offset="0%" stopColor="#2b1d63" />
        <stop offset="100%" stopColor="#1a1240" />
      </linearGradient>
      <linearGradient id="er-a5-hub" x1="0" y1="0" x2="1" y2="1">
        <stop offset="0%" stopColor="#3d2a0f" />
        <stop offset="100%" stopColor="#241a0c" />
      </linearGradient>
    </defs>
    <ellipse cx="86" cy="114" rx="84" ry="18" fill="url(#er-a5-glowV)" />
    <ellipse cx="236" cy="114" rx="84" ry="18" fill="url(#er-a5-glowA)" />
    <ellipse cx="86" cy="110" rx="70" ry="7" fill="url(#er-a5-stageV)" />
    <ellipse cx="236" cy="110" rx="70" ry="7" fill="url(#er-a5-stageA)" />
    <rect x="12" y="12" width="136" height="98" rx="12" fill="url(#er-a5-store)" stroke="#8b5cf6" strokeWidth="1.2" />
    <g transform="translate(24 22)" fill="none" stroke="#c4b5fd" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round">
      <path d="M1 2 h3 l2.3 11 h10.8 l1.9 -8 H5" />
      <circle cx="8" cy="17.5" r="1.6" fill="#c4b5fd" stroke="none" />
      <circle cx="14.5" cy="17.5" r="1.6" fill="#c4b5fd" stroke="none" />
    </g>
    <rect x="24" y="58" width="112" height="40" rx="8" fill="#efe9ff" />
    <rect x="32" y="66" width="22" height="22" rx="5" fill="#7c3aed" fillOpacity=".92" />
    <g transform="translate(37 71)" fill="#ffffff">
      <path d="M0 0 h12 v3 H0 Z" />
      <path d="M2 5 h8 v6 H2 Z" fillOpacity=".85" />
    </g>
    <rect x="62" y="69" width="62" height="5" rx="2.5" fill="#b6a5e6" />
    <rect x="62" y="81" width="44" height="5" rx="2.5" fill="#cbbdee" />
    <rect x="172" y="12" width="136" height="98" rx="12" fill="url(#er-a5-hub)" stroke="#f59e0b" strokeWidth="1.2" />
    <g transform="translate(184 22)" fill="none" stroke="#fcd34d" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round">
      <path d="M0 5 h16 l-1.5 12 H2.5 Z" />
      <path d="M4 5 V2.5 a4 4 0 0 1 8 0 V5" />
    </g>
    <rect x="216" y="30" width="52" height="12" rx="6" fill="#f59e0b" fillOpacity=".22" stroke="#f59e0b" strokeOpacity=".5" />
    <path d="M242 42 v6 M200 48 h84 M200 48 v12 M284 48 v12" stroke="#f59e0b" strokeWidth="1.3" fill="none" strokeLinecap="round" strokeOpacity=".6" />
    <rect x="182" y="60" width="112" height="20" rx="6" fill="#fdf2de" />
    <rect x="182" y="86" width="112" height="20" rx="6" fill="#fdf2de" />
    {[60, 86].map(y => <g key={y} transform={`translate(190 ${y + 3})`}>
      <rect width="14" height="14" rx="4" fill="#b45309" fillOpacity=".2" />
      <path d="M2 4.5 h10 v3.5 H2 Z" fill="#b45309" fillOpacity=".8" />
      <path d="M4.5 8 h5 v3.5 h-5 Z" fill="#b45309" fillOpacity=".5" />
    </g>)}
    {[0, 1].map(i => <g key={i}>
      <rect x="212" y={64 + i * 26} width="62" height="4.5" rx="2.2" fill="#d8bd92" />
      <rect x="212" y={72 + i * 26} width="42" height="4.5" rx="2.2" fill="#e4d2b3" />
    </g>)}
  </svg>;
}

/* ------------------------------------------------------------------ art 06 */
function ArtAccounts() {
  const { t } = useMarketing();
  const roles = [
    { key: 'pos', icon: ReceiptText, label: 'errolemerchant' },
    { key: 'storefront', icon: Store, label: 'errolestorefront' },
    { key: 'affiliate', icon: Users, label: 'erroleaffiliate' },
    { key: 'commercehub', icon: ShoppingBag, label: 'errolemarket' }
  ];
  return <div className="er-art er-art-roles">
    <ul className="er-roles">
      {roles.map(role => {
        const Icon = role.icon;
        return <li key={role.key} className={`er-role er-role-${role.key}`}>
          <span className="er-role-icon" aria-hidden="true"><Icon size={18} strokeWidth={1.9} /></span>
          <span className="er-role-label">{t(role.label)}</span>
        </li>;
      })}
    </ul>
    <p className="er-fee">
      <span className="er-fee-tag er-fee-affiliate"><Isolated text={t('ercommissions')} /></span>
      <span className="er-fee-sep">{t('erseparate')}</span>
      <span className="er-fee-tag er-fee-hub"><Isolated text={t('erfees')} /></span>
    </p>
  </div>;
}