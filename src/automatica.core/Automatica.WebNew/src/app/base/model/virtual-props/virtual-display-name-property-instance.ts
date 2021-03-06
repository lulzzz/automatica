import { PropertyInstance } from "../property-instance"
import { PropertyTemplate, PropertyTemplateType } from "../property-template"
import { PropertyType } from "../property-type"
import { VirtualPropertyInstance } from "./virtual-property-instance"
import { INameModel } from "../INameModel";

export class VirtualDisplayNamePropertyInstance extends VirtualPropertyInstance {

    /**
     *
     */
    constructor(private nodeInstance: INameModel, isReadonly: boolean = false) {
        super(nodeInstance);

        this.PropertyTemplate.Name = "COMMON.PROPERTY.NAME.NAME";
        this.PropertyTemplate.Description = "COMMON.PROPERTY.NAME.DESCRIPTION";
        this.PropertyTemplate.Key = "name";
        this.PropertyTemplate.IsReadonly = isReadonly;
    }

    get Value(): any {
        return this.nodeInstance.DisplayName;
    }
    set Value(value: any) {

        if (this.IsReadonly) {
            return;
        }

        this.nodeInstance.DisplayName = value;
        this.propertyChanged.emit(this);
    }


}
