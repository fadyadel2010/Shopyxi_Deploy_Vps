async (page) => {
  return page.context().pages().map((p,index)=>{
    const url=p.url().split('?')[0].split('#')[0],match=/^(https?:\/\/[^/]+)(\/.*)?$/.exec(url)
    return match?{index,origin:match[1],pathname:match[2]||'/'}:{index,origin:url}
  })
}
