export interface DbUser {
  id: string;
  username: string;
  email: string;
  password?: string;
  role: string;
  full_name: string | null;
  is_active: boolean;
  reset_token?: string | null;
  reset_token_exp?: Date | null;
  created_at: Date;
  updated_at: Date;
}

export interface SafeUser {
  id: string;
  username: string;
  email: string;
  role: string;
  fullName: string | null;
  isActive: boolean;
  createdAt: Date;
  updatedAt: Date;
}

export interface LoginResult {
  token: string;
  user: SafeUser;
}

export interface CarrierStaffUser {
  id: string;
  username: string;
  email: string;
  fullName: string | null;
  isActive: boolean;
  createdAt: Date;
}
