﻿
using System.Linq;
using FluentValidation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SWLOR.Game.Server.Data.Contracts;
using SWLOR.Game.Server.Data.Entity;
using SWLOR.Game.Server.Data.Validator;
using SWLOR.Game.Server.Enumeration;
using SWLOR.Game.Server.Service.Contracts;
using SWLOR.Game.Server.ValueObject;

namespace SWLOR.Game.Server.Data.Processor
{
    public class ApartmentBuildingProcessor: IDataProcessor<ApartmentBuilding>
    {
        public IValidator Validator => new ApartmentBuildingValidator();
        
        public DatabaseAction Process(IDataService data, JObject dataObject)
        {
            ApartmentBuilding apartmentBuilding = dataObject.ToObject<ApartmentBuilding>();
            var action = DatabaseActionType.Update;
            if(apartmentBuilding.ID <= 0)
            {
                int id = data.GetAll<ApartmentBuilding>().Count() + 1;
                apartmentBuilding.ID = id;
                action = DatabaseActionType.Insert;
            }
            
            return new DatabaseAction(apartmentBuilding, action);
        }
    }
}
