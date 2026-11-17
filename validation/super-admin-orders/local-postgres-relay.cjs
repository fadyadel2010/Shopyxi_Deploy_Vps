// Windows/WSL loopback-only transport for owned disposable PostgreSQL tests.
// No passwords are read; forwards bytes to Ubuntu's local isolated port 55432.
const net = require('node:net')
const { spawn } = require('node:child_process')
const python = `
import socket,select,os,sys
s=socket.create_connection(('127.0.0.1',55432))
while True:
    ready,_,_=select.select([s,sys.stdin.buffer],[],[])
    for source in ready:
        if source is s:
            data=s.recv(65536)
            if not data: sys.exit(0)
            sys.stdout.buffer.write(data);sys.stdout.buffer.flush()
        else:
            data=os.read(sys.stdin.fileno(),65536)
            if not data: sys.exit(0)
            s.sendall(data)
`
const server=net.createServer(client=>{
    const child=spawn('wsl',['-d','Ubuntu','--','python3','-u','-c',python],{stdio:['pipe','pipe','ignore']})
    client.pipe(child.stdin);child.stdout.pipe(client)
    child.stdin.on('error',()=>client.destroy())
    client.on('error',()=>child.kill())
    client.on('close',()=>child.kill())
    child.on('exit',()=>client.destroy())
})
server.listen(55432,'127.0.0.1',()=>console.log('Owned test relay: Windows loopback:55432 -> WSL loopback:55432'))
