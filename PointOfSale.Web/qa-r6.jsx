import React from 'react'
import { createRoot } from 'react-dom/client'
import i18n from 'i18next'
import { initReactI18next } from 'react-i18next'
import en from './src/i18n/locales/en.json'
import ar from './src/i18n/locales/ar.json'
import SalesHistory from './src/pages/SalesHistory'
import './src/index.css'

i18n.use(initReactI18next).init({ resources: { en: { translation: en }, ar: { translation: ar } }, lng: 'en', fallbackLng: 'en', interpolation: { escapeValue: false } })
createRoot(document.getElementById('root')).render(<><button onClick={() => i18n.changeLanguage(i18n.language === 'en' ? 'ar' : 'en')}>Switch language</button><SalesHistory /></>)
