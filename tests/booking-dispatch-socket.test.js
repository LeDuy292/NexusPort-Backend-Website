'use strict';
jest.mock('../src/models/User', () => ({User:{findByPk:jest.fn()}}));
const http = require('http');
const {io:client} = require('socket.io-client');
const {User} = require('../src/models/User');
const {generateToken} = require('../src/utils/jwt');
const {initSocket,emitToDriver,emitToDispatchers} = require('../src/config/socket');
let server,io,url;
const sockets=[];
const accounts={login1:{is_active:true,role:'Driver'},login2:{is_active:true,role:'Driver'},dispatcher:{is_active:true,role:'Dispatcher'},disabled:{is_active:false,role:'Driver'}};
const connect = (id,role) => new Promise((resolve,reject)=>{
  const socket=client(url,{auth:{token:generateToken({id,role})},transports:['websocket'],reconnection:false});
  sockets.push(socket);
  socket.once('connect',()=>resolve(socket));socket.once('connect_error',reject);
});
beforeAll(async()=>{
  User.findByPk.mockImplementation(id=>Promise.resolve(accounts[id]));
  server=http.createServer();io=initSocket(server);
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  url='http://127.0.0.1:'+server.address().port;
});
afterAll(async()=>{sockets.forEach(s=>s.disconnect());await new Promise(resolve=>io.close(resolve));});
test('booking events reach dispatchers and the assigned login only',async()=>{
  const [driver,other,dispatcher]=await Promise.all([connect('login1','Driver'),connect('login2','Driver'),connect('dispatcher','Dispatcher')]);
  const otherEvents=[];other.on('booking:dispatch_updated',e=>otherEvents.push(e));
  const receive = socket=>new Promise((resolve,reject)=>{const timer=setTimeout(()=>reject(new Error('Missing socket event')),2000);socket.once('booking:dispatch_updated',e=>{clearTimeout(timer);resolve(e);});});
  const pending=[receive(driver),receive(dispatcher)];
  const payload={eventId:'event-1',bookingId:'booking-1',version:1};
  emitToDriver('login1','booking:dispatch_updated',payload);emitToDispatchers('booking:dispatch_updated',payload);
  expect(await Promise.all(pending)).toEqual([payload,payload]);
  await new Promise(resolve=>setTimeout(resolve,50));
  expect(otherEvents).toEqual([]);
});
test('inactive accounts and stale roles cannot connect',async()=>{
  await expect(connect('disabled','Driver')).rejects.toThrow('không còn hợp lệ');
  await expect(connect('login1','Dispatcher')).rejects.toThrow('không còn hợp lệ');
});
