import {
  Injectable,
  UnauthorizedException,
} from '@nestjs/common';
import { JwtService } from '@nestjs/jwt';
import { ConfigService } from '@nestjs/config';
import { UserService } from './user.service';

export interface LoginDto {
  email: string;
  password: string;
}

export interface AuthResult {
  user: {
    id: string;
    email: string;
    firstName: string;
    lastName: string;
    roles: string[];
    permissions?: string[];
    employeeId?: string;
  };
  accessToken: string;
  refreshToken: string;
  expiresIn: number;
}

@Injectable()
export class AuthService {
  constructor(
    private readonly jwtService: JwtService,
    private readonly configService: ConfigService,
    private readonly userService: UserService,
  ) {}

  /**
   * Validate user credentials
   */
  async validateUser(email: string, password: string): Promise<any> {
    const user = await this.userService.findByEmail(email);

    if (!user) {
      return null;
    }

    if (!user.isActive) {
      throw new UnauthorizedException('Account is deactivated');
    }

    const isPasswordValid = await user.comparePassword(password);

    if (!isPasswordValid) {
      return null;
    }

    // Update last login
    await this.userService.updateLastLogin(user.id);

    // Return user without password
    const { password: _, ...result } = user;

    return {
      ...result,
      roles: user.roles || [],
    };
  }

  /**
   * Login user
   */
  async login(loginDto: LoginDto): Promise<AuthResult> {
    const user = await this.validateUser(loginDto.email, loginDto.password);

    if (!user) {
      throw new UnauthorizedException('Invalid credentials');
    }

    // Generate session ID
    const sessionId = `sess_${Date.now()}_${Math.random().toString(36).substring(7)}`;

    // Generate tokens
    const expiresIn = parseInt(
      this.configService.get<string>('JWT_EXPIRES_IN') || '900',
      10,
    ); // Default 15 minutes

    const accessToken = this.jwtService.sign(
      {
        sub: user.id,
        email: user.email,
        roles: user.roles || [],
        sessionId,
        type: 'access',
      },
      {
        expiresIn,
      },
    );

    const refreshToken = this.jwtService.sign(
      {
        sub: user.id,
        sessionId,
        type: 'refresh',
      },
      {
        expiresIn: this.configService.get<string>('JWT_REFRESH_EXPIRES_IN') || '7d',
      },
    );

    // Get user permissions
    const permissions = await this.userService.getUserPermissions(user.id);

    return {
      user: {
        id: user.id,
        email: user.email,
        firstName: user.firstName,
        lastName: user.lastName,
        roles: user.roles || [],
        permissions,
        employeeId: user.employeeId || undefined,
      },
      accessToken,
      refreshToken,
      expiresIn,
    };
  }
}

