'use strict';
jest.mock('../src/models/User',()=>({User:{findByPk:jest.fn()}}));
jest.mock('../src/utils/jwt',()=>({verifyToken:jest.fn(()=>({id:'account',role:'Driver'}))}));
jest.mock('../src/modules/auth/auth.service',()=>({}));
const express=require('express');
const request=require('supertest');
const {User}=require('../src/models/User');
const {authenticate}=require('../src/middlewares/authenticate');
const {getMeController}=require('../src/modules/auth/auth.controller');
const app=express();app.get('/me',authenticate,getMeController);
app.use((error,_req,res,_next)=>res.status(500).json({message:error.message}));
beforeEach(()=>jest.clearAllMocks());
test('me reuses middleware account: one fresh DB lookup per request, no secrets returned',async()=>{
  User.findByPk.mockResolvedValue({id:'account',is_active:true,password:'private',toSafeObject:()=>({id:'account',username:'driver'})});
  const res=await request(app).get('/me').set('Authorization','Bearer valid');
  expect(res.status).toBe(200);expect(User.findByPk).toHaveBeenCalledTimes(1);
  expect(res.body.data.user).toEqual({id:'account',username:'driver'});
  expect(res.body.data.user.password).toBeUndefined();
  await request(app).get('/me').set('Authorization','Bearer valid');
  expect(User.findByPk).toHaveBeenCalledTimes(2);
});
test('disabling the account takes effect on the very next heartbeat',async()=>{
  User.findByPk.mockResolvedValue({id:'account',is_active:false});
  expect((await request(app).get('/me').set('Authorization','Bearer valid')).status).toBe(401);
  expect(User.findByPk).toHaveBeenCalledTimes(1);
});
test('anonymous requests do not access the DB',async()=>{
  expect((await request(app).get('/me')).status).toBe(401);
  expect(User.findByPk).not.toHaveBeenCalled();
});
