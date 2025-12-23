import { Injectable, UnauthorizedException } from '@nestjs/common';
import { PassportStrategy } from '@nestjs/passport';
import { ExtractJwt, Strategy } from 'passport-jwt';
import { ConfigService } from '@nestjs/config';
import { UserService } from '../services/user.service';

export interface AccessTokenPayload {
  sub: string; // user ID
  email: string;
  roles: string[];
  sessionId: string;
  type: 'access';
}

@Injectable()
export class JwtStrategy extends PassportStrategy(Strategy) {
  constructor(
    private configService: ConfigService,
    private userService: UserService,
  ) {
    const secret = configService.get<string>('JWT_SECRET');
    if (!secret) {
      throw new Error(
        'JWT_SECRET is required. Set it in adherence backend .env/.env.local before starting the server.',
      );
    }

    super({
      jwtFromRequest: ExtractJwt.fromAuthHeaderAsBearerToken(),
      ignoreExpiration: false,
      secretOrKey: secret,
    });
  }

  async validate(payload: AccessTokenPayload) {
    // Validate that this is an access token
    if (payload.type !== 'access') {
      throw new UnauthorizedException('Invalid token type');
    }

    // Fetch user from database to ensure they're still active
    const user = await this.userService.findById(payload.sub);

    if (!user) {
      throw new UnauthorizedException('User not found');
    }

    if (!user.isActive) {
      throw new UnauthorizedException('User account is deactivated');
    }

    // Get fresh permissions from database
    const permissions = await this.userService.getUserPermissions(payload.sub);

    // Get fresh roles from database (not from token payload)
    const freshRoles = user.roles || [];

    return {
      userId: payload.sub,
      email: payload.email,
      roles: freshRoles, // Use fresh roles from DB
      permissions: permissions,
      sessionId: payload.sessionId,
      employeeId: user.employeeId || undefined,
    };
  }
}

