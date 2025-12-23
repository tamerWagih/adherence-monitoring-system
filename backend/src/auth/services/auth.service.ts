import {
  Injectable,
  UnauthorizedException,
} from '@nestjs/common';
import { JwtService } from '@nestjs/jwt';
import { ConfigService } from '@nestjs/config';
import { ApiProperty } from '@nestjs/swagger';
import { UserService } from './user.service';

export class LoginDto {
  @ApiProperty({ example: 'system.admin@test.com' })
  email: string;

  @ApiProperty({ example: 'Test123!', format: 'password' })
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

  private parseExpiresInToSeconds(expiresIn: string | number | undefined): number {
    if (expiresIn === undefined || expiresIn === null) return 15 * 60;

    if (typeof expiresIn === 'number') {
      return expiresIn;
    }

    const value = String(expiresIn).trim();
    if (!value) return 15 * 60;

    // If it's purely numeric, treat as seconds
    if (/^\d+$/.test(value)) {
      return parseInt(value, 10);
    }

    const match = value.match(/^(\d+)\s*([smhd])$/i);
    if (!match) {
      // Fallback to 15 minutes if format is unexpected (keeps server usable)
      return 15 * 60;
    }

    const amount = parseInt(match[1], 10);
    const unit = match[2].toLowerCase();
    switch (unit) {
      case 's':
        return amount;
      case 'm':
        return amount * 60;
      case 'h':
        return amount * 60 * 60;
      case 'd':
        return amount * 24 * 60 * 60;
      default:
        return 15 * 60;
    }
  }

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
    const expiresInSetting = this.configService.get<string>('JWT_EXPIRES_IN') || '15m';
    const expiresInSeconds = this.parseExpiresInToSeconds(expiresInSetting);

    const accessToken = this.jwtService.sign(
      {
        sub: user.id,
        email: user.email,
        roles: user.roles || [],
        sessionId,
        type: 'access',
      },
      {
        // IMPORTANT: allow standard formats like '15m', '1h', etc.
        expiresIn: expiresInSetting,
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
      expiresIn: expiresInSeconds,
    };
  }
}

