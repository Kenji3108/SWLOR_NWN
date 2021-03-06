﻿using FluentValidation;
using SWLOR.Game.Server.Data.Entity;

namespace SWLOR.Game.Server.Data.Validator
{
    public class LootTableValidator : AbstractValidator<LootTable>
    {
        public LootTableValidator()
        {
            RuleFor(x => x.Name)
                .NotNull()
                .NotEmpty()
                .MaximumLength(64);
        }
    }
}
