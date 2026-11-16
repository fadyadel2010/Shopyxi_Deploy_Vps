async (page) => {
  return await page.evaluate(() => ({
    origin: location.origin, pathname: location.pathname, search: location.search,
    analyticsNavigation: [...document.querySelectorAll('a,button')]
      .filter(e => /analytics|تحليلات|التحليلات/i.test(e.textContent || ''))
      .map(e => ({tag:e.tagName,label:e.textContent.trim().slice(0,80),href:e.getAttribute('href')}))
  }))
}
